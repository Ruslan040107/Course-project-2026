using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class LevelTexturing
{
    const string TexturesPath = "Assets/Textures";
    const string MaterialsPath = "Assets/Materials";
    const string MeshesPath = "Assets/Meshes/Level";

    // how many meters one texture covers
    const float FloorTile = 4f;
    const float WallTile = 4f;
    const float BorderTile = 4f;
    const float BlockTile = 3f;

    [MenuItem("Tools/Apply Level Textures")]
    public static void Apply()
    {
        GameObject level = GameObject.Find("Level");
        if (level == null)
        {
            Debug.LogError("Object 'Level' not found in the scene.");
            return;
        }

        Material floor = GetMaterial("Floor", "floor");
        Material wall = GetMaterial("Wall", "wall");
        Material border = GetMaterial("Border", "border");
        Material block = GetMaterial("Block", "block");

        if (floor == null || wall == null || border == null || block == null)
        {
            Debug.LogError("Textures not found. Put floor, wall, border, block (png) into " + TexturesPath);
            return;
        }

        Directory.CreateDirectory(MeshesPath);

        int count = 0;

        foreach (Transform child in level.transform)
        {
            MeshFilter filter = child.GetComponent<MeshFilter>();
            MeshRenderer rend = child.GetComponent<MeshRenderer>();
            if (filter == null || rend == null) continue;

            string n = child.name.ToLower();
            Material mat;
            float tile;

            if (n.Contains("plane")) { mat = floor; tile = FloorTile; }
            else if (n.Contains("border")) { mat = border; tile = BorderTile; }
            else if (n.Contains("block") || n.Contains("shelter")) { mat = block; tile = BlockTile; }
            else { mat = wall; tile = WallTile; }

            filter.sharedMesh = BuildMesh(filter.sharedMesh, child.lossyScale, tile, child.name);
            rend.sharedMaterial = mat;
            EditorUtility.SetDirty(child.gameObject);
            count++;
        }

        AssetDatabase.SaveAssets();
        EditorSceneManager.MarkSceneDirty(level.scene);
        Debug.Log("Level textures applied to " + count + " objects.");
    }

    static Material GetMaterial(string name, string textureName)
    {
        Texture2D tex = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturesPath + "/" + textureName + ".png");
        if (tex == null) return null;

        string path = MaterialsPath + "/" + name + ".mat";
        Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);

        if (mat == null)
        {
            mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            AssetDatabase.CreateAsset(mat, path);
        }

        mat.SetTexture("_BaseMap", tex);
        mat.SetColor("_BaseColor", Color.white);
        mat.SetTextureScale("_BaseMap", Vector2.one);
        mat.SetFloat("_Smoothness", 0.15f);
        mat.SetFloat("_Metallic", 0f);
        EditorUtility.SetDirty(mat);
        return mat;
    }

    static Mesh BuildMesh(Mesh source, Vector3 scale, float tile, string objectName)
    {
        Mesh mesh = Object.Instantiate(source);
        mesh.name = objectName + "_uv";

        Vector3[] verts = mesh.vertices;
        Vector3[] normals = mesh.normals;
        Vector2[] uv = new Vector2[verts.Length];

        for (int i = 0; i < verts.Length; i++)
        {
            Vector3 p = Vector3.Scale(verts[i], scale);
            Vector3 nrm = normals[i];
            Vector3 a = new Vector3(Mathf.Abs(nrm.x), Mathf.Abs(nrm.y), Mathf.Abs(nrm.z));

            Vector2 coord;
            if (a.y >= a.x && a.y >= a.z) coord = new Vector2(p.x, p.z);
            else if (a.x >= a.z) coord = new Vector2(p.z, p.y);
            else coord = new Vector2(p.x, p.y);

            uv[i] = coord / tile;
        }

        mesh.uv = uv;

        string path = MeshesPath + "/" + objectName.Replace(' ', '_') + "_uv.asset";
        AssetDatabase.DeleteAsset(path);
        AssetDatabase.CreateAsset(mesh, path);
        return mesh;
    }
}
