
using UdonSharp;
using UnityEngine;
// using Rendering;
using VRC.SDKBase;
using VRC.Udon;
using VRC.SDK3.Data; 

// future more efficient system: 
//   - At start, break down mesh into parts, sorting triangles & associated data into chunks
//   - On Update, we place nearby instances randomly in their chunk, and unload distant instances
//   - we have a max # of instances, and store their data in arrays of this size

// ideas for LOD: 
//   - since instances are just stored in arrays, we have to be a bit creative
//   - instances close to the start of the arrays are higher priority
//   - new instances would replace the ones at the end of the arrays before being sorted in
//   - priority is determined by:
//      - distance
//      - how much the player is facing towards it
//      - some random per-instance modifier? just so they don't change at some visible threshold
//   - however, using the player's look direction would definitely make sorting slower; so the effect of this should be small (maybe limited to stuff only directly behind you)
//   - we can optimize sorting like this:
//      - instead of moving the data, keep another array of the same size containing just indices 
//      - this way, we only have to sort some ints, rather than copying the data itself
//      - however comma! DrawMeshInstanced() wants us to just feed it arrays of data,
//        so we'll have to make sure that every instance we want to see is sorted correctly -
//        or at the very least, placed before our priority threshold (see below).
//   
//   - DrawMeshInstanced() will take data from the start of the array, 
//     so we can give it a sorted array and say 'just render half of them' and it will render the half with higher priority.
//   - but how many should we render? 
//   - priority threshold: we can allow the player to set a threshold 0-1, which acts kind of like render distance.
//     in an array sorted by priority, this is easy - just find the index of the first instance past the threshold (binary search), 
//     and render that many instances.
//   - One obstacle though: as described earlier, we can optimize sorting using an array of indices instead of sorting the data.

public class InstanceManager : UdonSharpBehaviour
{
    // private struct OctreeNode {
    //     short childIDs[];
    // }
    // public GameObject instanceSource;
    public Mesh instanceMesh;
    public Material[] materials;
    public GameObject chunkSource;
    public float density;
    public bool worldSpaceScale;
    public UnityEngine.Rendering.ShadowCastingMode castShadows;
    public bool receiveShadows;
    public int layer;



    // currently, we are sending data over from udon graph because UdonSharp doesn't let us access vertex colors, bone weights, etc
    // public Vector3 instancePosition;
    // public Vector3 instanceNormal;
    
    public Vector3[] meshVertices;
    public Vector3[] meshNormals;
    public Color[] meshColors;
    public int[] meshTriangles;

    Matrix4x4[] matrices = {};
    MaterialPropertyBlock properties;

    DataList positions;
    DataList normals;

    Vector3 playerPosition;

    private int chunkSize = 8;
    private int id = 0;

    public int renderDistance = 32;

    // void Start() {
    //     // Vector3 origin = new Vector3(0, 0, 0);
    //     // chunks[origin.GetHashCode()] = 0;
    // }

    void Update() {
        playerPosition = Networking.LocalPlayer.GetPosition();
        // UnloadDistantChunks();
        // LoadNearbyChunks();
        for (int i = 0; i < materials.Length; i++) {
            VRCGraphics.DrawMeshInstanced(instanceMesh, i, materials[i], matrices, matrices.Length, properties, castShadows, receiveShadows, layer);
        }
    }


    // private void LoadNearbyChunks() {
    //     // for now, let's make a grid and then eliminate the corner chunks that are too far away
    //     // this is slow and we could probably go faster with some cellular automata kind of method, I've written something like this before
    //     // but honestly we don't need to load more than one chunk per frame soo once that's better organized this won't be a problem anyway
    //     
    //     int chunkDistance = renderDistance / chunkSize;
    //     Vector3 centerChunkPos = GetChunkPos(playerPosition);
    //     
    //     float chunkDistanceSquared = chunkDistance * chunkDistance;
    //     for (int x = 0-chunkDistance; x < chunkDistance; x++) {
    //         for (int y = 0-chunkDistance; y < chunkDistance; y++) {
    //             for (int z = 0-chunkDistance; z < chunkDistance; z++) {
    //                 Vector3 offset = new Vector3(x, y, z);
    //
    //                 // out of range? skip this one
    //                 // for performance reasons, avoid using sqrt by using sqrMagnitude
    //                 if (offset.sqrMagnitude > chunkDistanceSquared) continue;
    //                 
    //                 Vector3 chunkPos = GetChunkPos(chunkSize * offset + centerChunkPos);
    //                 int chunkHash = chunkPos.GetHashCode();
    //                 
    //                 loadChunk(chunkHash);
    //             }
    //         }
    //     }
    // }

    public void MakeInstances() {

        // TODO: make an array to map instances to chunks
        //      + a dictionary for the opposite
        // for bonus optimization, the data can be sorted so that all the instances in a chunk are stored next to each other in their respective arrays
        //   - for each chunk, we would only need to store the start index and instance count, and then the data can be easily accessed
        //   - consider using vrchat datadictionary binary search 

        DataList instanceData = new DataList();
        int totalInstances = 0;
        
        Debug.Log("makign instances");
        Debug.Log("mesh triangles: " + (meshTriangles.Length/3).ToString());

        for (int i = 0; i < meshTriangles.Length; i+=3) {
            Debug.Log(i);

            Vector3 v0 = meshVertices[meshTriangles[i]];
            Vector3 v1 = meshVertices[meshTriangles[i+1]];
            Vector3 v2 = meshVertices[meshTriangles[i+2]];
            
            Vector3 n0 = meshNormals[meshTriangles[i]];
            Vector3 n1 = meshNormals[meshTriangles[i+1]];
            Vector3 n2 = meshNormals[meshTriangles[i+2]];
            
            Color c0 = meshColors[meshTriangles[i]];
            Color c1 = meshColors[meshTriangles[i+1]];
            Color c2 = meshColors[meshTriangles[i+2]];


            // determine how many instances to place based on the area of the triangle
            float area;
            if (!worldSpaceScale) {
                area = Vector3.Cross(v1 - v0, v2 - v0).magnitude / 2f;
            } else {
                Vector3 sourceScale = transform.lossyScale;
                Vector3 edgeA = Vector3.Scale(v1 - v0, sourceScale);
                Vector3 edgeB = Vector3.Scale(v2 - v0, sourceScale);
                
                area = Vector3.Cross(edgeA, edgeB).magnitude / 2f;
            }

            // we will distribute instances randomly inside triangle, using color (red) as vertex-specific density
            // let's average out the redness of each vertex for overall triangle vcolor density
            // pretty sure that maths
            // TODO: 
            //  - density and scale retreived from bone weights instead of colors
            //  - use triangle index as seed

            float vertexColorDensity = (c0.r + c1.r + c2.r) / 3f;
            
            
            int instanceCount = (int)Mathf.Floor(density * area * vertexColorDensity);
            totalInstances += instanceCount;
            for (int j = 0; j < instanceCount; j++) {
                // weighted barycentric sampling with interpolated density!
                float u;
                float v;
                float w;
                while (true) {
                    // place point completely randomly
                    
                    // ... come to think of it, yknow what might be better? just placing the points randomly from the start (weighted based on mean triangle density) and just calculating a chance of if they get to stay or not 
                    // that would means we could choose a max # of points and just work up to that, aaand we can make our instance data arrays right off the bat and not have to count them
                    // TODO: ^that

                    float r1 = Random.value;
                    float r2 = Random.value;
                    if (r1 + r2 > 1f) { r1 = 1f - r1; r2 = 1f - r2; }

                    u = 1f - r1 - r2;
                    v = r1;
                    w = r2;
                    
                    // for now, just grab density from vertex color red channel
                    // could probably use bone weights too..
                    float d0 = c0.r;
                    float d1 = c1.r;
                    float d2 = c2.r;

                    float interpolatedDensity = u * d0 + v * d1 + w * d2;

                    if (Random.value < interpolatedDensity / density) {
                        break;
                    }
                }

                Vector3 instancePosition = u * v0 + v * v1 + w * v2;
                Vector3 instanceNormal = u * n0 + v * n1 + w * n2;
                Vector3 instanceScale = Vector3.one * (u * c0 + v * c1 + w * c2).g;
                Color instanceColor = u * c0 + v * c1 + w * c2;
                
                // find or create chunk
                GameObject chunkObject;
                Vector3 chunkPosition = GetChunkPos(instancePosition);
                string chunkHash = chunkPosition.GetHashCode().ToString();
                // if ((chunkObject = this.transform.Find(chunkHash).gameObject) != null) {
                //     chunkObject = Instantiate(chunkSource, this.transform);
                //     chunkObject.name = chunkHash;
                // }

                // instantiate as child of chunk
                // TODO: use position as seed so the roll for any instance is always the same
                // this needs to be done for every random thing btw
                float roll = Random.value * 360;
                Quaternion instanceOrientation = Quaternion.AngleAxis(roll, instanceNormal);
                
                WriteInstanceData(
                        ref instanceData,
                        instancePosition,
                        instanceOrientation,
                        instanceScale,
                        instanceNormal,
                        instanceColor
                );
            }

            Debug.Log("total instances: " + totalInstances.ToString());
        }
        ProcessInstanceData(ref instanceData);
    }

    private void WriteInstanceData(ref DataList dataList, Vector3 position, Quaternion orientation, Vector3 scale, Vector3 normal, Color color) {
        // yeah there's totally a better way to do this but the udonsharp doesn't have bracket initializers yet so im out of ideas
        dataList.Add(position.x);
        dataList.Add(position.y);
        dataList.Add(position.z);
        dataList.Add(orientation.w);
        dataList.Add(orientation.x);
        dataList.Add(orientation.y);
        dataList.Add(orientation.z);
        dataList.Add(scale.x);
        dataList.Add(scale.y);
        dataList.Add(scale.z);
        dataList.Add(normal.x);
        dataList.Add(normal.y);
        dataList.Add(normal.z);
        dataList.Add(color.r);
        dataList.Add(color.g);
        dataList.Add(color.b);
        dataList.Add(color.a);
    }

    private void ProcessInstanceData(ref DataList dataList) {
        if (dataList.Count == 0) {
            Debug.LogError("no instance data");
            return;
        }
        const int instanceDataLength = 17;
        int count = dataList.Count / instanceDataLength;
        Debug.Log("processing " + count.ToString() + " instances");
        matrices = new Matrix4x4[count];
        Vector4[] normals = new Vector4[count];
        Vector4[] colors = new Vector4[count];
        
        DataToken[] instanceData = dataList.ToArray();
        
        // for scaling to world space
        Vector3 sourceScale = transform.lossyScale;
        Vector3 invSourceScale = new Vector3(1f/sourceScale.x, 1f/sourceScale.y, 1f/sourceScale.z);
        
        int tokenIndex = 0;
        for (int i = 0; i < count; i++) {
            
            // make sure they're all the right type
            for (int j = 0; j < instanceDataLength; j++) {
                if (instanceData[j].TokenType != TokenType.Float) {
                    return; // TODO: log error
                }
            }

            // retreive data from array:
            Vector3 position = new Vector3(
                    instanceData[tokenIndex].Float,
                    instanceData[tokenIndex + 1].Float,
                    instanceData[tokenIndex + 2].Float
            );
            
            Quaternion orientation = new Quaternion(
                    instanceData[tokenIndex + 3].Float,
                    instanceData[tokenIndex + 4].Float,
                    instanceData[tokenIndex + 5].Float,
                    instanceData[tokenIndex + 6].Float
            );

            Vector3 scale = new Vector3(
                    instanceData[tokenIndex + 7].Float,
                    instanceData[tokenIndex + 8].Float,
                    instanceData[tokenIndex + 9].Float
            );
            
            if (worldSpaceScale) {
                scale = Vector3.Scale(scale, invSourceScale);
            }

            matrices[i] = transform.localToWorldMatrix * Matrix4x4.TRS(position, orientation, scale);
            
            // for shader properties: 
            normals[i] = new Vector4(
                    instanceData[tokenIndex + 10].Float,
                    instanceData[tokenIndex + 11].Float,
                    instanceData[tokenIndex + 12].Float
            );

            colors[i] = new Vector4(
                    instanceData[tokenIndex + 10].Float,
                    instanceData[tokenIndex + 11].Float,
                    instanceData[tokenIndex + 12].Float
            );
            
            tokenIndex += instanceDataLength;
        }
        
        properties = new MaterialPropertyBlock();
        properties.SetVectorArray("sourceNormals", normals);
        properties.SetVectorArray("sourceColors", colors);
    }

    public Vector3 GetChunkPos(Vector3 vector3) {
        return new Vector3(
            Mathf.Floor(vector3.x / chunkSize) * chunkSize,
            Mathf.Floor(vector3.y / chunkSize) * chunkSize,
            Mathf.Floor(vector3.z / chunkSize) * chunkSize);
    }
}
