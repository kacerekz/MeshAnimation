﻿using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;
using MeshAnimation.Animation;
using MeshAnimation.Clustering;
using OpenTkRenderer.Rendering.Meshes;
using OpenTkRenderer.Structs;
using System;
using System.Collections.Generic;
using Supercluster.KDTree;
using MeshAnimation.DataStructures;

namespace MeshAnimation.Optimization
{
    class SSDROptimizer : IOptimizer
    {
        public int boneCount = 9;
        public int maxIterations = 1000;
        int maxInits = 10;

        SkinningAnimation outAnim;
        IAnimation inAnim;

        public float sigEpsilon = 3;
        int initCount = 0;

        KDTree<double, int> tree;
        Vector<double>[][] corP;
        Vector<double>[][] corQ;

        bool reinitInLastUpdate;
        double lastE;

        /// <summary>
        /// Optimize animation
        /// </summary>
        /// <returns></returns>
        public SkinningAnimation Optimize(IAnimation inAnim)
        {
            // TODO two different paths for TVM and DMA

            initCount = 0;
            corP = new Vector<double>[inAnim.Frames.Length][];
            corQ = new Vector<double>[inAnim.Frames.Length][];
            for (int i = 0; i < corP.Length; i++)
            {
                corP[i] = new Vector<double>[boneCount];
                corQ[i] = new Vector<double>[boneCount];
                for (int j = 0; j < boneCount; j++)
                {
                    corP[i][j] = Vector<double>.Build.Dense(3);
                    corQ[i][j] = Vector<double>.Build.Dense(3);
                }
            }

            outAnim = new SkinningAnimation((ObjLoader)inAnim.RestPose, boneCount, inAnim.Frames.Length);
            this.inAnim = inAnim;

            // initialize
            Console.WriteLine("Init step");
            InitializationStep();

            // start loop
            int iteration = 0;
            while(true)
            {
                Console.WriteLine("Iteration " + iteration);

                reinitInLastUpdate = false;

                // update weights
                WeightUpdateStep();
                // update bone transformations
                BoneTransformUpdateStep();

                // check if converged
                if (CheckConvergence() || iteration > maxIterations)
                    break;

                iteration++;
            }

            CorrectRestPose();

            return outAnim;
        }

        /// <summary>
        /// Initialization step
        /// </summary>
        /// <param name="inAnim"> Input animated mesh sequence </param>
        /// <param name="outAnim"> Output skinning animation </param>
        private void InitializationStep()
        {
            Console.WriteLine("Clustering");
            // clustering
            KMeans km = Cluster(inAnim);

            Console.WriteLine("Preparing out animation");
            // clustering into weight map
            PrepareOutAnimation(inAnim, km);
            ComputeCors();
        }

        //TODO eh
        /// <summary>
        /// Compute pStar and qStar at the beginning of the optimization
        /// </summary>
        private void ComputeCors()
        {
            // for each frame separately
            for (int f = 0; f < inAnim.Frames.Length; f++)
            {
                // for each bone in a frame separately
                for (int b = 0; b < boneCount; b++)
                {
                    double boneWeightSum = ComputeSignificance(b);

                    // CoR coordinates
                    Vec3f pstar = new Vec3f();
                    Vec3f qstar = new Vec3f();
                    Dictionary<int, double> boneWeights = outAnim.VertexBoneWeights[b];
                    foreach (int key in boneWeights.Keys)
                    {
                        Vec3f multipV = inAnim.RestPose.Vertices[key].Multiplied((float)(boneWeights[key] * boneWeights[key]));
                        pstar.Add(multipV);

                        // deformation residual
                        Vec3f q = inAnim.RestPose.Vertices[key].Subtracted(RemainingDeformation(key, b, f));
                        Vec3f multipQ = q.Multiplied((float)boneWeights[key]);
                        qstar.Add(multipQ);
                    }
                    pstar.Divide((float)boneWeightSum);
                    qstar.Divide((float)boneWeightSum);

                  
                    // find optimum rotation and translation
                    Vector<double> pstarV = Vector.Build.Dense(new double[] { pstar.x, pstar.y, pstar.z });
                    Vector<double> qstarV = Vector.Build.Dense(new double[] { qstar.x, qstar.y, qstar.z });

                    corP[f][b] = pstarV;
                    corQ[f][b] = qstarV;
                }
            }
        }

        /// <summary>
        /// Create init clusters on vertices of animated mesh sequence
        /// </summary>
        /// <param name="inAnim"> Input animated mesh sequence </param>
        /// <returns> Kmeans implementation used for clustering</returns>
        private KMeans Cluster(IAnimation inAnim)
        {
            KMeans km = new KMeans();
            km.BoneCount = boneCount;

            ObjLoader[] objs = new ObjLoader[inAnim.Frames.Length];
            for (int f = 0; f < inAnim.Frames.Length; f++)
                objs[f] = (ObjLoader)inAnim.Frames[f];

            km.Cluster(objs);
            return km;
        }

        /// <summary>
        /// Put init clustering results stored in km into output animation outAnim
        /// </summary>
        /// <param name="inAnim"> Input animated mesh sequence </param>
        /// <param name="outAnim"> Output skinning animation </param>
        /// <param name="km"> Used kmeans implementation </param>
        private void PrepareOutAnimation(IAnimation inAnim, KMeans km)
        {
            // set rest pose
            outAnim.RestPose = (ObjLoader)inAnim.RestPose;

            // set weight map
            for (int i = 0; i < km.BoneClusters.Count; i++)
                for (int j = 0; j < km.BoneClusters[i].Length; j++)
                    outAnim.VertexBoneWeights[i].Add(km.BoneClusters[i][j], 1);

            // set transformations
            for (int f = 0; f < inAnim.Frames.Length; f++)
            {
                for (int i = 0; i < boneCount; i++)
                {
                    outAnim.Frames[f].BoneRotation[i] = km.tMatrices[f][i];
                    outAnim.Frames[f].BoneTranslation[i] = km.tVectors[f][i];
                }
            }

        }

        // TODO read!
        // pstar stejné pro všechny frames -> P stejné pro všechny frames
        // otočit cyklus -> pro všechny bones ve všech frames  a vyhodit P nad cyklus pro frames

        /// <summary>
        /// Bone rotation and translation update step
        /// </summary>
        private void BoneTransformUpdateStep()
        {
            Console.WriteLine("Bone update");

            // for each frame separately
            for (int f = 0; f < inAnim.Frames.Length; f++)
            {
                Console.WriteLine("frame " + f);

                // for each bone in a frame separately
                for (int b = 0; b < boneCount; b++)
                {
                    Console.WriteLine("bone " + b);

                    double boneWeightSum = ComputeSignificance(b);

                    Console.WriteLine(boneWeightSum);

                    // if bone is insignificant
                    if (boneWeightSum < sigEpsilon)
                    {
                        // re-initialize bone
                        bool res = ReInitializeBone(b);
                        if (res)
                            continue;
                    }

                    // TODO pStar prolly  the same in all frames

                    // CoR coordinates
                    Vec3f pstar = new Vec3f();
                    Vec3f qstar = new Vec3f();
                    Dictionary<int, double> boneWeights = outAnim.VertexBoneWeights[b];

                    foreach (int key in boneWeights.Keys)
                    {
                        Vec3f multipV = inAnim.RestPose.Vertices[key].Multiplied((float)(boneWeights[key]* boneWeights[key]));
                        pstar.Add(multipV);

                        // deformation residual
                        Vec3f q = inAnim.Frames[f].Vertices[key].Subtracted(RemainingDeformation(key, b, f));
                        Vec3f multipQ = q.Multiplied((float)boneWeights[key]);
                        qstar.Add(multipQ);

                        // Console.WriteLine(qstar.x + " " + qstar.y + " " + qstar.z);
                    }
                    pstar.Divide((float)boneWeightSum);
                    qstar.Divide((float)boneWeightSum);

                    // data matrices P and Q
                    Matrix<double> P = Matrix.Build.Dense(3, inAnim.RestPose.Vertices.Length);
                    Matrix<double> Q = Matrix.Build.Dense(3, inAnim.RestPose.Vertices.Length);
                    for (int i = 0; i < inAnim.RestPose.Vertices.Length; i++)
                    {
                        // vertex pos
                        Vec3f v = inAnim.RestPose.Vertices[i];
                        Vec3f vf = inAnim.Frames[f].Vertices[i];

                        // deformation residual
                        Vec3f q = vf.Subtracted(RemainingDeformation(i, b, f)); 

                        double weight = 0;
                        if (boneWeights.ContainsKey(i))
                            weight = boneWeights[i];

                        // remove translation
                        Vec3f pNew = v.Subtracted(pstar);
                        P.SetColumn(i, new double[] { weight * pNew.x, weight * pNew.y, weight * pNew.z });

                        Vec3f qNew = q.Subtracted(qstar.Multiplied((float)weight));
                        Q.SetColumn(i, new double[] { qNew.x, qNew.y, qNew.z });

                        // Console.WriteLine("p" + i + " " + weight * pNew.x + " " + weight * pNew.y + " " + weight * pNew.z);
                        // Console.WriteLine("q" + i + " " + qNew.x + " " + qNew.y + " " + qNew.z);
                    }

                    // SVD
                    // Console.WriteLine("Before svd");
                    Matrix<double> m = P * Q.Transpose();
                    var resSvd = m.Svd();
                    Matrix<double> UT = resSvd.U.Transpose();
                    Matrix<double> V = resSvd.VT.Transpose();
                    // Console.WriteLine("After svd");

                    // find optimum rotation and translation
                    Vector<double> pstarV = Vector.Build.Dense(new double[] { pstar.x, pstar.y, pstar.z });
                    Vector<double> qstarV = Vector.Build.Dense(new double[] { qstar.x, qstar.y, qstar.z });
                    Matrix<double> boneRotation = V * UT;
                    Vector<double> boneTranslation = qstarV - boneRotation * pstarV;

                    corP[f][b] = pstarV;
                    corQ[f][b] = qstarV;

                    // store iun outAnim
                    outAnim.Frames[f].BoneRotation[b] = boneRotation;
                    outAnim.Frames[f].BoneTranslation[b] = boneTranslation;
                }
            }

        }

        /// <summary>
        /// Compute the position of vertex v in frame f if it is transformed by all bones except b
        /// </summary>
        /// <param name="v"> Vertex </param>
        /// <param name="b"> Bone </param>
        /// <param name="f"> Frame </param>
        /// <returns> Alternative position of vertex </returns>
        private Vec3f RemainingDeformation(int v, int b, int f)
        {
            Frame frame = outAnim.Frames[f];
            Vec3f vertex = inAnim.RestPose.Vertices[v];
            Vector<double> vertexV = Vector.Build.Dense(new double[] { vertex.x, vertex.y, vertex.z });

            Vector<double> sum = Vector.Build.Dense(3);
            for (int i = 0; i < boneCount; i++)
            {
                if (i == b)
                    continue;

                // LBS
                if (outAnim.VertexBoneWeights[b].ContainsKey(v))
                {
                    double weight = outAnim.VertexBoneWeights[b][v];

                    Vector<double> translation = frame.BoneTranslation[i];
                    Matrix<double> rotation = frame.BoneRotation[i];

                    sum += weight * (rotation * vertexV + translation);
                }
            }

            Vec3f res = new Vec3f((float)sum[0], (float)sum[1], (float)sum[2]);
            return res;
        }

        /// <summary>
        /// Compute the significance of bone b in animation
        /// </summary>
        /// <param name="b"> Index of bone </param>
        /// <returns> Significance of bone </returns>
        private double ComputeSignificance(int b)
        {
            // go through all weights for bone b, sum of pow of weights assigned to this bone
            double res = 0;
            Dictionary<int, double> boneWeights =  outAnim.VertexBoneWeights[b];
            foreach (double value in boneWeights.Values)
                res += value * value;

            return res;
        }

        /// <summary>
        /// Re-initialization of insignificant bone
        /// </summary>
        /// <param name="b"> Bone to re-initialize </param>
        /// <returns> Returns false if unsuccessfull (aka a bone has been re-initialized too many times), true if successfull  </returns>
        private bool ReInitializeBone(int b)
        {
            Console.WriteLine("Re-init pending");

            // if (initCount > maxInits)
            //    return false;

            Console.WriteLine("Re-init in progress");

            reinitInLastUpdate = true;

            // find vertex with largest reconstruction error
            double max = double.MinValue;
            int maxIndex = -1;
            Vec3f centroid = new Vec3f();
            for (int i = 0; i < inAnim.RestPose.Vertices.Length; i++)
            {
                double recError = GetVertexReconstructionError(i);
                centroid.Add(inAnim.RestPose.Vertices[i]);

                if (max < recError)
                {
                    max = recError;
                    maxIndex = i;
                }
            }
            centroid.Divide(inAnim.RestPose.Vertices.Length);

            // find 20 nearest vertices to that vertex
            if (tree == null)
                tree = KDTree.BuildTree(inAnim.RestPose.Vertices);
            List<int> neighboursIndices = KDTree.GetNearest(maxIndex, 21, outAnim.RestPose.Vertices, tree);

            // remove vertices from weight map
            outAnim.VertexBoneWeights[b] = new Dictionary<int, double>();
            for (int i = 0; i < outAnim.VertexBoneWeights.Length; i++)
            {
                for (int j = 0; j < neighboursIndices.Count; j++)
                {
                    if (outAnim.VertexBoneWeights[i].ContainsKey(neighboursIndices[j]))
                        outAnim.VertexBoneWeights[i].Remove(neighboursIndices[j]);
                }
            }

            // assign bone to those vertices
            for (int j = 0; j < neighboursIndices.Count; j++)
                outAnim.VertexBoneWeights[b].Add(neighboursIndices[j], 1);

            // get coordinates of neighbours - rest pose
            List<Vec3f> neighboursPoints = new List<Vec3f>();
            for (int i = 0; i < inAnim.RestPose.Vertices.Length; i++)
            {
                Vec3f v = inAnim.RestPose.Vertices[i];
                if (neighboursIndices.Contains(i))
                    neighboursPoints.Add(v);
            }

            // re-init rotation and translation
            Kabsch k = new Kabsch();
            for (int f = 0; f < inAnim.Frames.Length; f++)
            {
                // get coordinates of neighbours - frame
                List<Vec3f> neighboursInPose = new List<Vec3f>();
                for (int i = 0; i < inAnim.RestPose.Vertices.Length; i++)
                {
                    Vec3f v = inAnim.Frames[f].Vertices[i];
                    if (neighboursIndices.Contains(i))
                        neighboursInPose.Add(v);
                }

                // re-initialize bone transformation and rotation using Kabsch algorithm
                Matrix<double> rot = k.SolveKabsch(neighboursPoints.ToArray(), neighboursInPose.ToArray());
                outAnim.Frames[f].BoneRotation[b] = rot;
                outAnim.Frames[f].BoneTranslation[b] = k.Translation;
            }

            // increase re-init counter
            initCount++;

            Console.WriteLine("Re-init done");

            return true;
        }

        /// <summary>
        /// Get reconstruction error of vertex i in animation
        /// </summary>
        /// <param name="i"> Vertex index </param>
        /// <returns> Reconstruction error for vertex i </returns>
        private double GetVertexReconstructionError(int i)
        {
            double sum = 0;
            Vec3f vRest = inAnim.RestPose.Vertices[i];
            Vector<double> vRestV = Vector.Build.Dense(new double[] { vRest.x, vRest.y, vRest.z });

            // go through all frames
            for (int f = 0; f < inAnim.Frames.Length; f++)
            {
                // get position in frame inAnim
                Vec3f v = inAnim.Frames[f].Vertices[i];

                // reconstruct position in frame outAnim
                Vector<double> posV = Vector.Build.Dense(3);
                for (int b = 0; b < boneCount; b++)
                {
                    if (outAnim.VertexBoneWeights[b].ContainsKey(i))
                    {
                        double weight = outAnim.VertexBoneWeights[b][i];

                        Vector<double> translation = outAnim.Frames[f].BoneTranslation[b];
                        Matrix<double> rotation = outAnim.Frames[f].BoneRotation[b];

                        posV += weight * (rotation * vRestV + translation);
                    }
                }

                // resulting error in frame
                Vec3f pos = new Vec3f((float)posV[0], (float)posV[1], (float)posV[2]);
                Vec3f resFrame = v.Subtracted(pos);

                // add error to sum
                sum += resFrame.x * resFrame.x + resFrame.y * resFrame.y + resFrame.z * resFrame.z;
            }
         
            return sum;
        }

        /// <summary>
        /// Update weights
        /// </summary>
        private void WeightUpdateStep()
        {
            Console.WriteLine("Weight update");

            for (int b = 0; b < outAnim.VertexBoneWeights.Length; b++)
                outAnim.VertexBoneWeights[b] = new Dictionary<int, double>();

            int vertexCount = outAnim.RestPose.Vertices.Length;
            int frameCount = outAnim.Frames.Length;
            int boneCount = this.boneCount;

            var V = Vector<double>.Build;

            for (int v = 0; v < vertexCount; v++)
            {
                var restVertex = outAnim.RestPose.Vertices[v].ToVector();
                var A = new double[3 * frameCount][];
                var b = new double[3 * frameCount];

                for (int frame = 0; frame < 3 * frameCount; frame++)
                {
                    A[frame] = new double[boneCount];
                }

                for (int frame = 0; frame < frameCount; frame++)
                {
                    // Fill A
                    for (int bone = 0; bone < boneCount; bone++)
                    {
                        Matrix<double> rotation = outAnim.Frames[frame].BoneRotation[bone];
                        Vector<double> translation = outAnim.Frames[frame].BoneTranslation[bone];
                        Vector<double> centerOfRotation = GetRestCoR(v, bone);

                        Vector<double> rotated = rotation * (restVertex - centerOfRotation);

                        A[3 * frame + 0][bone] = rotated[0] + translation[0];
                        A[3 * frame + 1][bone] = rotated[1] + translation[1];
                        A[3 * frame + 2][bone] = rotated[2] + translation[2];
                    }

                    // Fill B
                    b[3 * frame + 0] = inAnim.Frames[frame].Vertices[v].x;
                    b[3 * frame + 1] = inAnim.Frames[frame].Vertices[v].y;
                    b[3 * frame + 2] = inAnim.Frames[frame].Vertices[v].z;
                }

                // Solve system the first time
                var nnls = new NonNegativeLeastSquares();
                var regression = nnls.Learn(A, b);
                var weights = regression.Weights;

                // Retrieve the most significant weights
                double[] effects = new double[boneCount];

                for (int frame = 0; frame < frameCount; frame++)
                {
                    for (int bone = 0; bone < boneCount; bone++)
                    {
                        var x = weights[bone] * A[3 * frame + 0][bone];
                        var y = weights[bone] * A[3 * frame + 1][bone];
                        var z = weights[bone] * A[3 * frame + 2][bone];

                        var moved = V.Dense(new double[] {x, y, z});

                        effects[bone] += (moved - restVertex).L2Norm();
                    }
                }

                int[] indices = new int[boneCount];
                for (int i = 0; i < boneCount; i++)
                    indices[i] = i;

                Array.Sort(effects, indices);
                Array.Reverse(indices);

                // Recereate A
                var A_sig = new double[3 * frameCount][];

                for (int frame = 0; frame < 3 * frameCount; frame++)
                {
                    A_sig[frame] = new double[significantBoneCount];
                }

                for (int frame = 0; frame < frameCount; frame++)
                {
                    for (int sigBone = 0; sigBone < significantBoneCount; sigBone++)
                    {
                        A[3 * frame + 0][sigBone] = A[3 * frame + 0][indices[sigBone]];
                        A[3 * frame + 1][sigBone] = A[3 * frame + 1][indices[sigBone]];
                        A[3 * frame + 2][sigBone] = A[3 * frame + 2][indices[sigBone]];
                    }
                }

                // Solve again
                nnls = new NonNegativeLeastSquares();
                regression = nnls.Learn(A_sig, b);
                weights = regression.Weights;

                // Place weights to appropriate positions
                for (int sigBone = 0; sigBone < significantBoneCount; sigBone++)
                {
                    outAnim.VertexBoneWeights[indices[sigBone]][v] = weights[sigBone];                    
                }

            }
        }

        private Vector<double> GetRestCoR(int v, int bone)
        {
            return corP[0][bone];
            // throw new NotImplementedException();
        }

        /// <summary>
        /// Calculates the value of the objective function E of the animation
        /// </summary>
        /// <returns> Value of E </returns>
        private double ComputeObjectiveFunction()
        {

            // E = \sum_{t=1}^{|t|} \sum_{i=1}^{|V|} \| v_i^t - \sum_{j=1}^{|B|} w_{ij}(R_j^t p_i + T_j^t) \|^2

            double eVal = 0;
            for (int f = 0; f < outAnim.Frames.Length; f++)
            {
                for (int i = 0; i < outAnim.RestPose.Vertices.Length; i++)
                {
                    // input vertex position
                    Vector<double> vif = Vector<double>.Build.Dense(new double[] { inAnim.Frames[f].Vertices[i].x, inAnim.Frames[f].Vertices[i].y, inAnim.Frames[f].Vertices[i].z });

                    // reconstructed vertex position
                    Vector<double> ri = Vector<double>.Build.Dense(new double[] { outAnim.RestPose.Vertices[i].x, outAnim.RestPose.Vertices[i].y, outAnim.RestPose.Vertices[i].z });
                    Vector<double> pi = Vector<double>.Build.Dense(3, 0);

                    // LBS
                    for (int b = 0; b < boneCount; b++)
                    {
                        if (outAnim.VertexBoneWeights[b].ContainsKey(i))
                        {
                            double weight = outAnim.VertexBoneWeights[b][i];

                            pi += (weight * (outAnim.Frames[f].BoneRotation[b] * ri + outAnim.Frames[f].BoneTranslation[b]));
                        }
                    }

                    Vector<double> res = vif - pi;
                    eVal += res[0] * res[0] + res[1] * res[1] + res[2] * res[2];
                }
            }

            return eVal;
        }

        /// <summary>
        /// Check if reached convergence
        /// - if within one iteration the objective function E has not improved by 1% and the bone transformation reinitialization is not performed
        /// </summary>
        /// <returns></returns>
        private bool CheckConvergence()
        {
            if (reinitInLastUpdate || lastE == 0)
                return false;

            double onePerc = lastE / 100.0;
            double currE = ComputeObjectiveFunction();

            // improved by 1% or more
            if ((lastE - currE) >= onePerc)
            {
                lastE = currE;
                return true;
            }

            lastE = currE;
            return false;
        }

       
        private void CorrectRestPose()
        {
            // TODO 
            // throw new NotImplementedException();
        }
    }
}
