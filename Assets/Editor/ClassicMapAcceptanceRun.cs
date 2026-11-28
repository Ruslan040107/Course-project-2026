using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
public static class ClassicMapAcceptanceRun
{
    public static void Run()
    {
        ClassicMapBuilder.BuildBoth();
        foreach (var map in new[] { "Dust2", "Mirage" })
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/" + map + "Map.unity", OpenSceneMode.Single);
            Vector3 Point(string id) => GameObject.Find(id).transform.position;
            bool dust = map == "Dust2";
            // Render the saved level under its gameplay sun, fog and sky.
            var cam = new GameObject("Preview").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.Skybox;
            cam.nearClipPlane=.08f; cam.farClipPlane=400f;
            cam.transform.position=new Vector3(0, 145, -85);
            cam.transform.LookAt(Vector3.zero);
            cam.orthographic=true;
            cam.orthographicSize=100f;
            bool fog=RenderSettings.fog;
            RenderSettings.fog=false;
            cam.clearFlags=CameraClearFlags.SolidColor;
            cam.backgroundColor=new Color(.17f,.21f,.26f);
            Capture(cam,map+"-overview");
            RenderSettings.fog=fog;
            cam.clearFlags=CameraClearFlags.Skybox;
            cam.orthographic=false; cam.fieldOfView=64f;
            void View(string id, string target, string suffix, float height = 1.65f)
            {
                cam.transform.position = Point(id) + Vector3.up * height;
                cam.transform.LookAt(Point(target) + Vector3.up * 1.4f);
                Capture(cam, map + "-" + suffix);
            }
            if (dust) View("Route_Mid", "SiteA", "street");
            else
            {
                View("Spot_A05", "Spot_A12", "site-a");
                View("Spot_B13", "Spot_B16", "site-b");
                View("Spot_M10", "Spot_M08", "mid");
            }
            UnityEngine.Object.DestroyImmediate(cam.gameObject);
        }
        var result = BuildPipeline.BuildPlayer(new BuildPlayerOptions {
            scenes=EditorBuildSettings.scenes.Where(s=>s.enabled).Select(s=>s.path).ToArray(),
            locationPathName="Builds/ClassicMaps/Course project.exe",
            target=BuildTarget.StandaloneWindows64, options=BuildOptions.None });
        Debug.Log("Classic maps player build: "+result.summary.result+" errors="+result.summary.totalErrors);
        if(result.summary.result!=BuildResult.Succeeded) throw new Exception("Windows build failed");
    }
    static void Capture(Camera cam,string name)
    {
        Directory.CreateDirectory("Builds/ClassicMapPreviews");
        var rt=new RenderTexture(1200,760,24,RenderTextureFormat.ARGB32);
        var prev=RenderTexture.active;
        cam.targetTexture=rt; cam.Render(); cam.Render();
        RenderTexture.active=rt;
        var img=new Texture2D(rt.width,rt.height,TextureFormat.RGB24,false);
        img.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0); img.Apply();
        File.WriteAllBytes("Builds/ClassicMapPreviews/"+name+".png",img.EncodeToPNG());
        cam.targetTexture=null; RenderTexture.active=prev; rt.Release();
        UnityEngine.Object.DestroyImmediate(rt); UnityEngine.Object.DestroyImmediate(img);
    }
}

