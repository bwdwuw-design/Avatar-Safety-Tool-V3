// =====================================================================================
//  Avatar Slimmer  V3  (single file: AvatarSlimmerV3.cs)
//  Install : put this file inside a folder named "Editor" in your Unity project
//            (for example  Assets/Editor/AvatarSlimmerV3.cs).
//  Open    : menu  Tools > Avatar Slimmer V3
//  Notes   : no VRChat SDK reference is needed (SDK components are found by name), so the
//            file also compiles in a plain Unity project. Every change goes through Undo,
//            and a scene backup is taken before destructive operations.
// =====================================================================================
#if UNITY_EDITOR
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text;
using System;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEditor;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using UnityEngine;

namespace AvatarSlimmer3
{
    // ------------------------------------------------------------------ basic types
    internal enum Level { Ok = 0, Info = 1, Warn = 2, Danger = 3 }

    internal class Reason { public Level level; public string text; }

    internal class RiskInfo
    {
        public Level level;
        public string verdict = "";
        public List<Reason> reasons = new List<Reason>();
    }

    internal class Issue
    {
        public Level level;
        public string title;
        public string detail;
        public GameObject target;
        public string fixLabel;
        public Action fix;
        public int gotoTab = -1;
    }

    internal class RefEdge { public Transform owner; public Transform target; public Component comp; public string prop; }

    internal class AnimUse
    {
        public string path; public string type; public string prop; public string clip;
        public bool objectRef; public bool fromComponent;
    }

    internal class Part
    {
        public GameObject go;
        public string path = "";
        public string kind = "";
        public Renderer renderer;
        public Mesh mesh;
        public int tris;
        public int slots;
        public int extraRenderers;
        public bool tick;
        public bool ignored;
    }

    internal class TexRow
    {
        public Texture2D tex;
        public string path = "";
        public int w, h;
        public long vram;
        public string category = "Other";
        public bool inPackages, uncompressed, hq, crunch, sensitive, isNormal;
        public bool flatChecked, flat;
        public int target;              // 0 = leave as is
        public bool fixCompression;
        public bool apply;
        public HashSet<Renderer> users = new HashSet<Renderer>();
        public HashSet<string> props = new HashSet<string>();
        public HashSet<string> userNames = new HashSet<string>();
        public int srcMax, impMax;               // size of the source image / current import limit
        public AstTexLogEntry orig;              // how it looked before this tool first changed it
        public bool changedByTool;
        public bool planCrunch;                  // the plan wants Crunch compression on it
    }

    internal class MeshInfo
    {
        public Mesh mesh;
        public string assetPath = "";
        public long bytes;
        public int blendCount;
        public bool readable;
        public List<Renderer> users = new List<Renderer>();
        public List<SkinnedMeshRenderer> smrs = new List<SkinnedMeshRenderer>();
    }

    internal class ScanData
    {
        public GameObject avatar;
        public List<Component> comps = new List<Component>();
        public List<RefEdge> refs = new List<RefEdge>();
        public List<AnimUse> animUses = new List<AnimUse>();
        public Dictionary<string, List<AnimUse>> animByPath = new Dictionary<string, List<AnimUse>>();
        public HashSet<string> extraBlendNames = new HashSet<string>();
        public Dictionary<string, string> stringOwner = new Dictionary<string, string>();
        public HashSet<Transform> humanoid = new HashSet<Transform>();
        public bool hasVRCFury, hasModularAvatar, hasD4rkOptimizer;
        public Component descriptor;
        public List<Part> parts = new List<Part>();
        public Dictionary<GameObject, Part> partByGo = new Dictionary<GameObject, Part>();
        public List<TexRow> textures = new List<TexRow>();
        public List<MeshInfo> meshes = new List<MeshInfo>();
        public List<AnimatorController> fxControllers = new List<AnimatorController>();
        public List<string> layerProblems = new List<string>();
        public List<Issue> issues = new List<Issue>();
        public List<string[]> stats = new List<string[]>();
        public Dictionary<GameObject, RiskInfo> riskCache = new Dictionary<GameObject, RiskInfo>();
        public int totalTris, activeTris, slotCount, smrCount, meshRendererCount, boneCount;
        public long texBytes, meshBytes, audioBytes;
        public List<string> audioNames = new List<string>();
        public long animBytes;
        public long staleBytes;
        public List<StaleRef> staleRefs = new List<StaleRef>();
        public List<AudioClip> audioClipList = new List<AudioClip>();
        public HashSet<RuntimeAnimatorController> extraControllers = new HashSet<RuntimeAnimatorController>();
        public HashSet<AnimationClip> extraClips = new HashSet<AnimationClip>();
    }

    // ------------------------------------------------------------------ settings (persisted per machine)
    internal static class AstSettings
    {
        const string P = "AvatarSlimmer2.";
        static bool GetB(string k, bool d) { return EditorPrefs.GetBool(P + k, d); }
        static void SetB(string k, bool v) { EditorPrefs.SetBool(P + k, v); }
        static int GetI(string k, int d) { return EditorPrefs.GetInt(P + k, d); }
        static void SetI(string k, int v) { EditorPrefs.SetInt(P + k, v); }

        public static bool AutoBackup { get { return GetB("autoBackup", true); } set { SetB("autoBackup", value); } }
        public static int BackupKeep { get { return GetI("backupKeep", 10); } set { SetI("backupKeep", Mathf.Clamp(value, 1, 50)); } }
        public static bool ProtectVrcShapes { get { return GetB("protVrc", true); } set { SetB("protVrc", value); } }
        public static bool ProtectMmdShapes { get { return GetB("protMmd", true); } set { SetB("protMmd", value); } }
        public static string KeepShapes { get { return EditorPrefs.GetString(P + "keepShapes", ""); } set { EditorPrefs.SetString(P + "keepShapes", value ?? ""); } }
        public static bool PlainThumbs { get { return GetB("plainThumbs", false); } set { SetB("plainThumbs", value); } }
        public static bool SmartCaps { get { return GetB("smartCaps", true); } set { SetB("smartCaps", value); } }
        public static int SingleCap { get { return GetI("singleCap", 2048); } set { SetI("singleCap", value); } }
        public static int CapColor { get { return GetI("capColor", 2048); } set { SetI("capColor", value); } }
        public static int CapNormal { get { return GetI("capNormal", 1024); } set { SetI("capNormal", value); } }
        public static int CapMask { get { return GetI("capMask", 1024); } set { SetI("capMask", value); } }
        public static int CapEmission { get { return GetI("capEmission", 1024); } set { SetI("capEmission", value); } }
        public static int CapMatCap { get { return GetI("capMatCap", 512); } set { SetI("capMatCap", value); } }
        public static int CapOther { get { return GetI("capOther", 1024); } set { SetI("capOther", value); } }
        public static bool FixCompression { get { return GetB("fixCompression", true); } set { SetB("fixCompression", value); } }
        public static bool HqToNormal { get { return GetB("hqToNormal", true); } set { SetB("hqToNormal", value); } }
        public static bool ProtectFace { get { return GetB("protectFace", true); } set { SetB("protectFace", value); } }
        public static float TargetMB { get { return EditorPrefs.GetFloat(P + "targetMB", 20f); } set { EditorPrefs.SetFloat(P + "targetMB", Mathf.Max(0.5f, value)); } }
        public static int TargetBasis { get { return GetI("targetBasis", 0); } set { SetI("targetBasis", value); } }
        public static bool SmartStart { get { return GetB("smartStart", true); } set { SetB("smartStart", value); } }
        public static int CrunchQuality { get { return GetI("crunchQ", 50); } set { SetI("crunchQ", Mathf.Clamp(value, 10, 100)); } }
    }

    // ------------------------------------------------------------------ session log
    internal static class AstLog
    {
        public static readonly List<string> Lines = new List<string>();
        public static void Add(string msg)
        {
            Lines.Add(DateTime.Now.ToString("HH:mm:ss") + "   " + msg);
            if (Lines.Count > 300) Lines.RemoveAt(0);
            Debug.Log("[Avatar Slimmer] " + msg);
        }
    }

    // ------------------------------------------------------------------ utilities
    internal static class AstUtil
    {
        public const string Root = "Assets/AvatarSlimmer";

        public static string MB(long bytes) { return (bytes / 1048576.0).ToString("0.00") + " MB"; }

        public static string Names(IEnumerable<string> names, int max)
        {
            var list = names.Distinct().ToList();
            string s = string.Join(", ", list.Take(max).ToArray());
            return list.Count > max ? s + ", ..." : s;
        }

        public static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "Unnamed";
            var sb = new StringBuilder();
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
            return sb.ToString();
        }

        public static void EnsureFolder(string assetFolder)
        {
            if (AssetDatabase.IsValidFolder(assetFolder)) return;
            string parent = Path.GetDirectoryName(assetFolder).Replace('\\', '/');
            string leaf = Path.GetFileName(assetFolder);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        public static string PathTo(Transform t, Transform root)
        {
            return AnimationUtility.CalculateTransformPath(t, root);
        }

        public static bool LockedByPrefab(GameObject go)
        {
            return PrefabUtility.IsPartOfPrefabInstance(go) && PrefabUtility.GetNearestPrefabInstanceRoot(go) != go;
        }

        public static Component FindComp(GameObject root, string typeName)
        {
            foreach (var c in root.GetComponentsInChildren<Component>(true))
                if (c != null && c.GetType().Name == typeName) return c;
            return null;
        }

        public static long MemOf(UnityEngine.Object o)
        {
            if (o == null) return 0;
            try { return Profiler.GetRuntimeMemorySizeLong(o); } catch (Exception) { return 0; }
        }

        public static string Rank(double v, double ex, double good, double med, double poor)
        {
            if (v <= ex) return "Excellent";
            if (v <= good) return "Good";
            if (v <= med) return "Medium";
            if (v <= poor) return "Poor";
            return "Very Poor";
        }

        public static Color RankColor(string rank)
        {
            switch (rank)
            {
                case "Excellent": return new Color(0.35f, 0.85f, 0.45f);
                case "Good": return new Color(0.55f, 0.85f, 0.35f);
                case "Medium": return new Color(1f, 0.85f, 0.3f);
                case "Poor": return new Color(1f, 0.6f, 0.25f);
                case "Very Poor": return new Color(1f, 0.38f, 0.33f);
                default: return Color.white;
            }
        }

        public static Color LevelColor(Level l)
        {
            switch (l)
            {
                case Level.Ok: return new Color(0.45f, 0.85f, 0.45f);
                case Level.Info: return new Color(0.6f, 0.8f, 1f);
                case Level.Warn: return new Color(1f, 0.8f, 0.3f);
                default: return new Color(1f, 0.4f, 0.35f);
            }
        }

        public static string LevelText(Level l)
        {
            switch (l)
            {
                case Level.Ok: return "SAFE";
                case Level.Info: return "PROBABLY OK";
                case Level.Warn: return "CAUTION";
                default: return "DANGER";
            }
        }

        public static Texture Icon(string name)
        {
            try
            {
                var c = EditorGUIUtility.IconContent(name);
                return c != null ? c.image : null;
            }
            catch (Exception) { return null; }
        }
    }
}

namespace AvatarSlimmer3
{
    internal static class AstScan
    {
        static void Prog(Action<string, float> p, string msg, float f) { if (p != null) p(msg, f); }

        public static ScanData Run(GameObject avatar, Action<string, float> progress)
        {
            var S = new ScanData();
            S.avatar = avatar;
            Prog(progress, "Reading components...", 0.02f);
            CollectComponents(S);
            BuildRefs(S, progress);
            CollectHumanoid(S);
            Prog(progress, "Reading animations...", 0.55f);
            CollectAnimations(S);
            Prog(progress, "Analysing parts...", 0.65f);
            BuildParts(S);
            BuildMeshes(S);
            Prog(progress, "Analysing textures...", 0.8f);
            AstTex.LoadOriginals();
            BuildTextures(S);
            BuildAudio(S);
            S.staleRefs = AstMat.FindStale(S);
            S.staleBytes = AstMat.StaleBytes(S, S.staleRefs);
            Prog(progress, "Running checks...", 0.92f);
            AstPreflight.Run(S);
            return S;
        }

        // ---------------------------------------------------------------- components
        static void CollectComponents(ScanData S)
        {
            foreach (var c in S.avatar.GetComponentsInChildren<Component>(true))
                if (c != null) S.comps.Add(c);
            S.descriptor = S.comps.FirstOrDefault(c => c.GetType().Name == "VRCAvatarDescriptor");
            S.hasD4rkOptimizer = S.comps.Any(c => c.GetType().Name == "d4rkAvatarOptimizer");
        }

        static Transform ToTransform(UnityEngine.Object o)
        {
            if (o is Transform) return (Transform)o;
            if (o is Component) return ((Component)o).transform;
            if (o is GameObject) return ((GameObject)o).transform;
            return null;
        }

        // Every serialized "pointer" from a component to another object inside the avatar,
        // plus every text value (blend shape names are often stored as text by VRCFury etc).
        static void BuildRefs(ScanData S, Action<string, float> progress)
        {
            var avRoot = S.avatar.transform;
            for (int i = 0; i < S.comps.Count; i++)
            {
                var c = S.comps[i];
                if (c == null || c is Transform) continue;
                string tn = c.GetType().Name;
                string ns = c.GetType().Namespace ?? "";
                if (ns.StartsWith("VF.", StringComparison.Ordinal)) S.hasVRCFury = true;
                if (ns.StartsWith("nadena.dev.modular_avatar", StringComparison.Ordinal)) S.hasModularAvatar = true;
                if (i % 25 == 0) Prog(progress, "Reading components " + i + "/" + S.comps.Count, 0.05f + 0.5f * i / S.comps.Count);
                try
                {
                    var so = new SerializedObject(c);
                    var it = so.GetIterator();
                    bool enter = true;
                    while (it.Next(enter))
                    {
                        enter = true;
                        switch (it.propertyType)
                        {
                            case SerializedPropertyType.String:
                                {
                                    enter = false;
                                    string sv = it.stringValue;
                                    if (!string.IsNullOrEmpty(sv) && sv.Length <= 160 && !S.stringOwner.ContainsKey(sv))
                                        S.stringOwner[sv] = tn;
                                    break;
                                }
                            case SerializedPropertyType.AnimationCurve:
                            case SerializedPropertyType.Gradient:
                                enter = false;
                                break;
                            case SerializedPropertyType.ObjectReference:
                                {
                                    var o = it.objectReferenceValue;
                                    if (o == null) break;
                                    var rc = o as RuntimeAnimatorController;
                                    if (rc != null) { S.extraControllers.Add(rc); break; }
                                    var ac = o as AnimationClip;
                                    if (ac != null) { S.extraClips.Add(ac); break; }
                                    var t = ToTransform(o);
                                    if (t == null || t == avRoot || t == c.transform) break;
                                    if (!t.IsChildOf(avRoot)) break;
                                    S.refs.Add(new RefEdge { owner = c.transform, target = t, comp = c, prop = it.propertyPath });
                                    break;
                                }
                        }
                    }
                }
                catch (Exception) { /* some components cannot be serialized; skip them */ }
            }
        }

        static void CollectHumanoid(ScanData S)
        {
            var anim = S.avatar.GetComponent<Animator>();
            if (anim == null || !anim.isHuman) return;
            foreach (HumanBodyBones b in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (b == HumanBodyBones.LastBone) continue;
                var t = anim.GetBoneTransform(b);
                if (t != null) S.humanoid.Add(t);
            }
        }

        // ---------------------------------------------------------------- animations
        static void CollectAnimations(ScanData S)
        {
            var controllers = new List<KeyValuePair<string, RuntimeAnimatorController>>();
            if (S.descriptor != null)
            {
                var so = new SerializedObject(S.descriptor);
                foreach (var arrName in new[] { "baseAnimationLayers", "specialAnimationLayers" })
                {
                    var arr = so.FindProperty(arrName);
                    if (arr == null || !arr.isArray) continue;
                    for (int i = 0; i < arr.arraySize; i++)
                    {
                        var el = arr.GetArrayElementAtIndex(i);
                        string label = arrName + "[" + i + "]";
                        try
                        {
                            var tp = el.FindPropertyRelative("type");
                            if (tp != null && tp.propertyType == SerializedPropertyType.Enum) label = tp.enumNames[tp.enumValueIndex];
                        }
                        catch (Exception) { }
                        var ctrlProp = el.FindPropertyRelative("animatorController");
                        var defProp = el.FindPropertyRelative("isDefault");
                        var ctrl = ctrlProp != null ? ctrlProp.objectReferenceValue as RuntimeAnimatorController : null;
                        bool isDefault = defProp == null || defProp.boolValue;
                        if (ctrl != null)
                        {
                            controllers.Add(new KeyValuePair<string, RuntimeAnimatorController>(label, ctrl));
                            if (label.Contains("FX"))
                            {
                                var ac = ctrl as AnimatorController;
                                if (ac == null && ctrl is AnimatorOverrideController)
                                    ac = ((AnimatorOverrideController)ctrl).runtimeAnimatorController as AnimatorController;
                                if (ac != null) S.fxControllers.Add(ac);
                            }
                        }
                        else if (!isDefault)
                        {
                            S.layerProblems.Add(label);
                        }
                    }
                }
            }
            var rootAnimator = S.avatar.GetComponent<Animator>();
            if (rootAnimator != null && rootAnimator.runtimeAnimatorController != null)
                controllers.Add(new KeyValuePair<string, RuntimeAnimatorController>("Animator", rootAnimator.runtimeAnimatorController));

            var seen = new HashSet<AnimationClip>();
            foreach (var kv in controllers)
                foreach (var clip in kv.Value.animationClips)
                    AddClip(S, clip, seen, false);
            foreach (var rc in S.extraControllers)
                foreach (var clip in rc.animationClips)
                    AddClip(S, clip, seen, true);
            foreach (var clip in S.extraClips)
                AddClip(S, clip, seen, true);

            foreach (var u in S.animUses)
            {
                List<AnimUse> list;
                if (!S.animByPath.TryGetValue(u.path, out list)) { list = new List<AnimUse>(); S.animByPath[u.path] = list; }
                list.Add(u);
            }
        }

        static void AddClip(ScanData S, AnimationClip clip, HashSet<AnimationClip> seen, bool fromComponent)
        {
            if (clip == null || !seen.Add(clip)) return;
            S.animBytes += AstUtil.MemOf(clip);
            foreach (var b in AnimationUtility.GetCurveBindings(clip))
            {
                S.animUses.Add(new AnimUse { path = b.path, type = b.type != null ? b.type.Name : "", prop = b.propertyName, clip = clip.name, objectRef = false, fromComponent = fromComponent });
                if (fromComponent && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                    S.extraBlendNames.Add(b.propertyName.Substring("blendShape.".Length));
            }
            foreach (var b in AnimationUtility.GetObjectReferenceCurveBindings(clip))
                S.animUses.Add(new AnimUse { path = b.path, type = b.type != null ? b.type.Name : "", prop = b.propertyName, clip = clip.name, objectRef = true, fromComponent = fromComponent });
        }

        // ---------------------------------------------------------------- parts / meshes
        public static int CountTris(Mesh m)
        {
            if (m == null) return 0;
            long n = 0;
            for (int i = 0; i < m.subMeshCount; i++)
                if (m.GetTopology(i) == MeshTopology.Triangles) n += m.GetIndexCount(i) / 3;
            return (int)n;
        }

        static Part GetPart(ScanData S, GameObject go)
        {
            Part p;
            if (!S.partByGo.TryGetValue(go, out p))
            {
                p = new Part();
                p.go = go;
                p.path = AstUtil.PathTo(go.transform, S.avatar.transform);
                S.partByGo[go] = p;
                S.parts.Add(p);
            }
            return p;
        }

        static string AddKind(string kinds, string k)
        {
            if (string.IsNullOrEmpty(kinds)) return k;
            return kinds.Contains(k) ? kinds : kinds + "+" + k;
        }

        public static Mesh MeshOf(Renderer r)
        {
            var smr = r as SkinnedMeshRenderer;
            if (smr != null) return smr.sharedMesh;
            if (r is MeshRenderer)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null) return mf.sharedMesh;
            }
            return null;
        }

        static void BuildParts(ScanData S)
        {
            foreach (var r in S.avatar.GetComponentsInChildren<Renderer>(true))
            {
                if (r.gameObject == S.avatar) continue;
                string kind = r is SkinnedMeshRenderer ? "Skinned mesh" : r is MeshRenderer ? "Mesh" : r is ParticleSystemRenderer ? "Particles" : r.GetType().Name;
                var mesh = MeshOf(r);
                var p = GetPart(S, r.gameObject);
                p.kind = AddKind(p.kind, kind);
                if (p.renderer == null) { p.renderer = r; p.mesh = mesh; }
                p.tris += CountTris(mesh);
                p.slots += r.sharedMaterials.Length;
            }
            foreach (var l in S.avatar.GetComponentsInChildren<Light>(true))
            {
                if (l.gameObject == S.avatar) continue;
                var p = GetPart(S, l.gameObject); p.kind = AddKind(p.kind, "Light");
            }
            foreach (var a in S.avatar.GetComponentsInChildren<AudioSource>(true))
            {
                if (a.gameObject == S.avatar) continue;
                var p = GetPart(S, a.gameObject); p.kind = AddKind(p.kind, "Audio");
            }
            S.parts = S.parts.OrderByDescending(p => p.tris).ToList();
            foreach (var p in S.parts)
                p.extraRenderers = p.go.GetComponentsInChildren<Renderer>(true).Length - p.go.GetComponents<Renderer>().Length;
        }

        static void BuildMeshes(ScanData S)
        {
            var map = new Dictionary<Mesh, MeshInfo>();
            foreach (var r in S.avatar.GetComponentsInChildren<Renderer>(true))
            {
                var m = MeshOf(r);
                if (m == null) continue;
                MeshInfo mi;
                if (!map.TryGetValue(m, out mi))
                {
                    mi = new MeshInfo();
                    mi.mesh = m;
                    mi.assetPath = AssetDatabase.GetAssetPath(m);
                    mi.bytes = AstUtil.MemOf(m);
                    mi.blendCount = m.blendShapeCount;
                    mi.readable = m.isReadable;
                    map[m] = mi;
                    S.meshes.Add(mi);
                }
                mi.users.Add(r);
                var smr = r as SkinnedMeshRenderer;
                if (smr != null) mi.smrs.Add(smr);
            }
            S.meshBytes = S.meshes.Sum(m => m.bytes);
        }

        // ---------------------------------------------------------------- audio
        static void BuildAudio(ScanData S)
        {
            var seen = new HashSet<AudioClip>();
            foreach (var a in S.avatar.GetComponentsInChildren<AudioSource>(true))
            {
                if (a.clip == null || !seen.Add(a.clip)) continue;
                long b = AstUtil.MemOf(a.clip);
                S.audioBytes += b;
                S.audioClipList.Add(a.clip);
                S.audioNames.Add(a.clip.name + " (" + AstUtil.MB(b) + ")");
            }
        }

        // ---------------------------------------------------------------- textures
        static void BuildTextures(ScanData S)
        {
            var map = new Dictionary<Texture2D, TexRow>();
            foreach (var r in S.avatar.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat == null || mat.shader == null) continue;
                    foreach (var prop in mat.GetTexturePropertyNames())
                    {
                        var t = mat.GetTexture(prop) as Texture2D;
                        if (t == null) continue;
                        TexRow row;
                        if (!map.TryGetValue(t, out row))
                        {
                            row = AstTex.BuildRow(t);
                            if (row == null) continue;
                            map[t] = row;
                        }
                        row.users.Add(r);
                        row.userNames.Add(r.name);
                        row.props.Add(prop);
                    }
                }
            }
            S.textures = map.Values.OrderByDescending(x => x.vram).ToList();
            foreach (var row in S.textures) AstTex.Classify(row);
            S.texBytes = S.textures.Sum(x => x.vram);
        }
    }
}

namespace AvatarSlimmer3
{
    // ---- change logs (one JSON file per batch, so everything can be reverted later) ----
    [Serializable]
    public class AstTexLogEntry
    {
        public string path;
        public int oldMax;
        public int oldCompression;
        public bool oldCrunch;
        public int oldQuality;
        public bool psOverridden;
        public int psOldMax;
        public int psOldCompression;
        public bool psOldCrunch;
        public int psOldQuality;
    }

    [Serializable]
    public class AstMatLogEntry
    {
        public string materialPath;
        public string prop;
        public string texturePath;     // the texture this slot held BEFORE the change
    }

    [Serializable]
    public class AstAudioLogEntry
    {
        public string path;
        public int oldFormat;
        public float oldQuality;
        public bool oldMono;
    }

    [Serializable]
    public class AstChangeLog
    {
        public string created;
        public string avatar;
        public List<AstTexLogEntry> entries = new List<AstTexLogEntry>();     // textures (name kept for older logs)
        public List<AstMatLogEntry> matRefs = new List<AstMatLogEntry>();
        public List<AstAudioLogEntry> audio = new List<AstAudioLogEntry>();
        public bool IsEmpty { get { return entries.Count == 0 && matRefs.Count == 0 && audio.Count == 0; } }
    }

    internal static class AstTex
    {
        static readonly string[] SensitiveNames = { "face", "head", "eye", "mouth", "teeth", "lash", "brow", "iris", "pupil", "tongue" };
        public static readonly string[] Categories = { "Color", "Normal", "Mask", "Emission", "MatCap", "Other" };

        // how every texture looked before this tool first touched it (loaded from the logs)
        public static Dictionary<string, AstTexLogEntry> Originals = new Dictionary<string, AstTexLogEntry>();

        public static string ProjectPathOf(string assetPath)
        {
            string data = Application.dataPath;
            return data.Substring(0, data.Length - "Assets".Length) + assetPath;
        }

        public static void LoadOriginals()
        {
            Originals = new Dictionary<string, AstTexLogEntry>();
            var logs = ListLogs();
            logs.Reverse();    // oldest first, so the first entry per texture is its true original
            foreach (var p in logs)
            {
                var log = ReadLog(p);
                if (log == null) continue;
                foreach (var e in log.entries)
                    if (!Originals.ContainsKey(e.path)) Originals[e.path] = e;
            }
        }

        // ------------------------------------------------------------ analysis
        public static TextureImporterCompression EffectiveCompression(TextureImporter imp)
        {
            var ps = imp.GetPlatformTextureSettings("Standalone");
            return ps.overridden ? ps.textureCompression : imp.textureCompression;
        }

        public static TexRow BuildRow(Texture2D t)
        {
            string path = AssetDatabase.GetAssetPath(t);
            if (string.IsNullOrEmpty(path)) return null;
            var imp = AssetImporter.GetAtPath(path) as TextureImporter;
            if (imp == null) return null;
            var row = new TexRow();
            row.tex = t;
            row.path = path;
            row.w = t.width;
            row.h = t.height;
            row.vram = AstUtil.MemOf(t);
            row.inPackages = path.StartsWith("Packages/", StringComparison.Ordinal);
            var comp = EffectiveCompression(imp);
            row.uncompressed = comp == TextureImporterCompression.Uncompressed;
            row.hq = comp == TextureImporterCompression.CompressedHQ;
            row.crunch = imp.crunchedCompression;
            row.isNormal = imp.textureType == TextureImporterType.NormalMap;
            row.impMax = imp.maxTextureSize;
            try
            {
                int sw, sh;
                imp.GetSourceTextureWidthAndHeight(out sw, out sh);
                row.srcMax = Mathf.Max(sw, sh);
            }
            catch (Exception) { row.srcMax = Mathf.Max(t.width, t.height); }
            AstTexLogEntry o;
            if (Originals.TryGetValue(path, out o))
            {
                row.orig = o;
                row.changedByTool = o.oldMax != imp.maxTextureSize || o.oldCompression != (int)imp.textureCompression || o.oldCrunch != imp.crunchedCompression;
            }
            return row;
        }

        public static string CategoryOfProp(string prop)
        {
            string p = prop.ToLowerInvariant();
            if (p.Contains("bump") || p.Contains("normal")) return "Normal";
            if (p.Contains("emis") || p.Contains("glow")) return "Emission";
            if (p.Contains("matcap")) return "MatCap";
            if (p.Contains("mask") || p.Contains("metal") || p.Contains("gloss") || p.Contains("rough") || p.Contains("occl")
                || p.Contains("spec") || p.Contains("height") || p.Contains("parallax") || p.Contains("detail")) return "Mask";
            if (p.Contains("main") || p.Contains("base") || p.Contains("albedo") || p.Contains("diffuse") || p.Contains("color") || p.Contains("shadow")) return "Color";
            return "Other";
        }

        public static int CapFor(string category)
        {
            if (!AstSettings.SmartCaps) return AstSettings.SingleCap;
            switch (category)
            {
                case "Color": return AstSettings.CapColor;
                case "Normal": return AstSettings.CapNormal;
                case "Mask": return AstSettings.CapMask;
                case "Emission": return AstSettings.CapEmission;
                case "MatCap": return AstSettings.CapMatCap;
                default: return AstSettings.CapOther;
            }
        }

        public static void Classify(TexRow row)
        {
            var cats = new HashSet<string>();
            foreach (var p in row.props) cats.Add(CategoryOfProp(p));
            if (row.isNormal) cats.Add("Normal");
            string best = "Other";
            int bestCap = -1;
            foreach (var c in cats)
            {
                int cap = CapFor(c);
                if (cap > bestCap) { bestCap = cap; best = c; }
            }
            row.category = best;
            string lower = (string.Join(" ", row.userNames.ToArray()) + " " + row.tex.name).ToLowerInvariant();
            row.sensitive = AstSettings.ProtectFace && SensitiveNames.Any(n => lower.Contains(n));
            Plan(row);
        }

        // decides the suggested target size / compression fix for one row (Textures tab "smart limits")
        public static void Plan(TexRow row)
        {
            int cap = CapFor(row.category);
            int longest = Mathf.Max(row.w, row.h);
            row.target = (!row.inPackages && longest > cap) ? cap : 0;
            if (row.flat && !row.inPackages && longest > 32) row.target = 32;
            row.fixCompression = !row.inPackages && ((AstSettings.FixCompression && row.uncompressed) || (AstSettings.HqToNormal && row.hq));
            row.planCrunch = false;
            bool wants = row.target > 0 || row.fixCompression;
            // face / eye / head textures and flat-colour candidates are listed, but only changed when the user ticks them
            row.apply = wants && !row.sensitive && !row.flat;
        }

        public static void ReplanAll(IEnumerable<TexRow> rows)
        {
            foreach (var r in rows)
            {
                bool wasFlat = r.flat;
                Classify(r);
                r.flat = wasFlat;
                Plan(r);
            }
        }

        // memory after the change (only meaningful for rows that will be applied)
        public static long After(TexRow r)
        {
            long v = r.vram;
            int longest = Mathf.Max(r.w, r.h);
            if (r.target > 0 && longest > 0)
            {
                float f = (float)r.target / longest;
                v = (long)(v * f * f);
            }
            if (r.fixCompression) v = (long)(v * (r.uncompressed ? 0.25 : 0.5));
            return v;
        }

        public static long AfterIfApplied(TexRow r) { return r.apply ? After(r) : r.vram; }

        // ------------------------------------------------------------ flat colour detection
        public static bool IsFlat(Texture2D tex)
        {
            RenderTexture rt = null;
            Texture2D read = null;
            var prev = RenderTexture.active;
            try
            {
                rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                read = new Texture2D(64, 64, TextureFormat.RGBA32, false, true);
                read.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                var px = read.GetPixels32();
                int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0, minA = 255, maxA = 0;
                for (int i = 0; i < px.Length; i++)
                {
                    var c = px[i];
                    if (c.r < minR) minR = c.r; if (c.r > maxR) maxR = c.r;
                    if (c.g < minG) minG = c.g; if (c.g > maxG) maxG = c.g;
                    if (c.b < minB) minB = c.b; if (c.b > maxB) maxB = c.b;
                    if (c.a < minA) minA = c.a; if (c.a > maxA) maxA = c.a;
                }
                return (maxR - minR) <= 1 && (maxG - minG) <= 1 && (maxB - minB) <= 1 && (maxA - minA) <= 1;
            }
            catch (Exception) { return false; }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (read != null) UnityEngine.Object.DestroyImmediate(read);
            }
        }

        public static int DetectFlat(IList<TexRow> rows, Action<string, float> progress)
        {
            int found = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (progress != null) progress("Checking " + r.tex.name, (float)i / Mathf.Max(1, rows.Count));
                if (r.flatChecked) { if (r.flat) found++; continue; }
                r.flatChecked = true;
                if (Mathf.Max(r.w, r.h) < 128 || r.tex.mipmapCount <= 1 || r.inPackages) continue;
                r.flat = IsFlat(r.tex);
                if (r.flat) { found++; Plan(r); }
            }
            return found;
        }

        // ------------------------------------------------------------ apply / revert
        // Changes the import settings of the given rows and records how they looked before. Returns how many changed.
        public static int ApplyRows(IEnumerable<TexRow> rows, AstChangeLog log)
        {
            int changedCount = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var r in rows)
                {
                    if (!r.apply || r.inPackages) continue;
                    var imp = AssetImporter.GetAtPath(r.path) as TextureImporter;
                    if (imp == null) continue;
                    var e = new AstTexLogEntry();
                    e.path = r.path;
                    e.oldMax = imp.maxTextureSize;
                    e.oldCompression = (int)imp.textureCompression;
                    e.oldCrunch = imp.crunchedCompression;
                    e.oldQuality = imp.compressionQuality;
                    var ps = imp.GetPlatformTextureSettings("Standalone");
                    e.psOverridden = ps.overridden;
                    e.psOldMax = ps.maxTextureSize;
                    e.psOldCompression = (int)ps.textureCompression;
                    e.psOldCrunch = ps.crunchedCompression;
                    e.psOldQuality = ps.compressionQuality;
                    bool changed = false, psChanged = false;
                    if (r.target > 0)
                    {
                        if (imp.maxTextureSize != r.target) { imp.maxTextureSize = r.target; changed = true; }
                        if (ps.overridden && ps.maxTextureSize != r.target) { ps.maxTextureSize = r.target; psChanged = true; changed = true; }
                    }
                    if (r.fixCompression)
                    {
                        if (imp.textureCompression == TextureImporterCompression.Uncompressed || imp.textureCompression == TextureImporterCompression.CompressedHQ)
                        { imp.textureCompression = TextureImporterCompression.Compressed; changed = true; }
                        if (ps.overridden && (ps.textureCompression == TextureImporterCompression.Uncompressed || ps.textureCompression == TextureImporterCompression.CompressedHQ))
                        { ps.textureCompression = TextureImporterCompression.Compressed; psChanged = true; changed = true; }
                    }
                    if (r.planCrunch && !imp.crunchedCompression)
                    {
                        if (imp.textureCompression == TextureImporterCompression.Uncompressed) imp.textureCompression = TextureImporterCompression.Compressed;
                        imp.crunchedCompression = true;
                        imp.compressionQuality = AstSettings.CrunchQuality;
                        changed = true;
                        if (ps.overridden) { ps.crunchedCompression = true; ps.compressionQuality = AstSettings.CrunchQuality; psChanged = true; }
                    }
                    if (psChanged) imp.SetPlatformTextureSettings(ps);
                    if (changed)
                    {
                        imp.SaveAndReimport();
                        log.entries.Add(e);
                        changedCount++;
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
            return changedCount;
        }

        public static AstChangeLog NewLog(ScanData S)
        {
            var log = new AstChangeLog();
            log.avatar = S.avatar.name;
            log.created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            return log;
        }

        // old API used by the Textures tab: change + write its own log
        public static string Apply(ScanData S, List<TexRow> rows)
        {
            var log = NewLog(S);
            ApplyRows(rows, log);
            if (log.IsEmpty) return null;
            return WriteLog(log, "changes");
        }

        public static string WriteLog(AstChangeLog log, string kind)
        {
            AstUtil.EnsureFolder(AstUtil.Root + "/History");
            string asset = AstUtil.Root + "/History/" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + kind + ".json";
            File.WriteAllText(ProjectPathOf(asset), JsonUtility.ToJson(log, true));
            AssetDatabase.ImportAsset(asset);
            return asset;
        }

        public static List<string> ListLogs()
        {
            var res = new List<string>();
            string folder = AstUtil.Root + "/History";
            if (!AssetDatabase.IsValidFolder(folder)) return res;
            foreach (var guid in AssetDatabase.FindAssets("", new[] { folder }))
            {
                string p = AssetDatabase.GUIDToAssetPath(guid);
                if (p.EndsWith("_textures.json", StringComparison.Ordinal) || p.EndsWith("_changes.json", StringComparison.Ordinal)) res.Add(p);
            }
            res.Sort(StringComparer.Ordinal);
            res.Reverse();     // newest first
            return res;
        }

        public static AstChangeLog ReadLog(string assetPath)
        {
            try { return JsonUtility.FromJson<AstChangeLog>(File.ReadAllText(ProjectPathOf(assetPath))); }
            catch (Exception) { return null; }
        }

        static void RestoreEntry(AstTexLogEntry e)
        {
            var imp = AssetImporter.GetAtPath(e.path) as TextureImporter;
            if (imp == null) return;
            imp.maxTextureSize = e.oldMax;
            imp.textureCompression = (TextureImporterCompression)e.oldCompression;
            imp.crunchedCompression = e.oldCrunch;
            imp.compressionQuality = e.oldQuality;
            if (e.psOverridden)
            {
                var ps = imp.GetPlatformTextureSettings("Standalone");
                ps.maxTextureSize = e.psOldMax;
                ps.textureCompression = (TextureImporterCompression)e.psOldCompression;
                ps.crunchedCompression = e.psOldCrunch;
                ps.compressionQuality = e.psOldQuality;
                imp.SetPlatformTextureSettings(ps);
            }
            imp.SaveAndReimport();
        }

        // puts ONE texture back the way it was before the tool first changed it
        public static bool RestoreOne(TexRow r)
        {
            if (r == null || r.orig == null) return false;
            AssetDatabase.StartAssetEditing();
            try { RestoreEntry(r.orig); }
            finally { AssetDatabase.StopAssetEditing(); AssetDatabase.Refresh(); }
            return true;
        }

        // undoes a whole batch: textures, material slots, audio. Returns the number of things restored.
        public static int Revert(string assetPath)
        {
            var log = ReadLog(assetPath);
            if (log == null) return 0;
            int n = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var e in log.entries) { RestoreEntry(e); n++; }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
            n += AstMat.RestoreRefs(log.matRefs);
            n += AstAudio.Restore(log.audio);
            AssetDatabase.SaveAssets();
            return n;
        }
    }
}

namespace AvatarSlimmer3
{
    internal struct Savings
    {
        public long tex, mesh;
        public int tris, slots, objects;
    }

    // ===================================================================== risk of deleting something
    internal static class AstRisk
    {
        static void Add(RiskInfo ri, Level l, string text) { ri.reasons.Add(new Reason { level = l, text = text }); }

        public static RiskInfo Evaluate(ScanData S, GameObject go)
        {
            RiskInfo ri;
            if (go == null) return new RiskInfo();
            if (S.riskCache.TryGetValue(go, out ri)) return ri;
            ri = new RiskInfo();
            S.riskCache[go] = ri;
            var t = go.transform;

            if (go == S.avatar)
            {
                Add(ri, Level.Danger, "This is the avatar root itself.");
                Finish(ri, go);
                return ri;
            }
            if (S.descriptor != null && S.descriptor.transform.IsChildOf(t))
                Add(ri, Level.Danger, "It contains the VRC Avatar Descriptor.");

            int total = go.GetComponentsInChildren<Renderer>(true).Length;
            int own = go.GetComponents<Renderer>().Length;
            if (total - own > 0)
                Add(ri, Level.Warn, "Deleting it also deletes " + (total - own) + " other mesh/renderer object(s) under it.");

            if (S.humanoid.Any(b => b != null && b.IsChildOf(t)))
                Add(ri, Level.Danger, "It contains the humanoid body bones (hips/spine/head/...). Never delete this.");

            // things that point at it from outside
            var ext = S.refs.Where(e => e.target != null && e.owner != null && e.target.IsChildOf(t) && !e.owner.IsChildOf(t)).ToList();
            foreach (var grp in ext.GroupBy(e => e.comp.GetType().Name))
            {
                string typeName = grp.Key;
                var first = grp.First().comp;
                string ns = first.GetType().Namespace ?? "";
                var owners = grp.Select(e => e.owner.name);
                int count = grp.Select(e => e.comp).Distinct().Count();
                if (typeName.Contains("AvatarDescriptor"))
                    Add(ri, Level.Danger, "The VRC Avatar Descriptor points to it (visemes / eyelids / eye bones / etc).");
                else if (first is SkinnedMeshRenderer)
                    Add(ri, Level.Danger, "Other skinned mesh(es) use bones from it: " + AstUtil.Names(owners, 3) + ". They would deform wrongly.");
                else if (typeName.Contains("PhysBone"))
                    Add(ri, Level.Warn, "PhysBone (" + count + ") uses it: " + AstUtil.Names(owners, 3) + ". That PhysBone would break.");
                else if (typeName.Contains("Constraint"))
                    Add(ri, Level.Warn, "Constraint (" + count + ") uses it: " + AstUtil.Names(owners, 3) + ".");
                else if (typeName.Contains("Contact"))
                    Add(ri, Level.Warn, "Contact (" + count + ") uses it: " + AstUtil.Names(owners, 3) + ".");
                else if (ns.StartsWith("VF.", StringComparison.Ordinal))
                    Add(ri, Level.Warn, "VRCFury component (" + typeName + ") points to it: " + AstUtil.Names(owners, 3) + ".");
                else if (ns.StartsWith("nadena.dev.modular_avatar", StringComparison.Ordinal))
                    Add(ri, Level.Warn, "Modular Avatar component (" + typeName + ") points to it: " + AstUtil.Names(owners, 3) + ".");
                else
                    Add(ri, Level.Warn, typeName + " (" + count + ") points to it: " + AstUtil.Names(owners, 3) + ".");
            }

            // animations that use it (or something under it)
            string P = AstUtil.PathTo(t, S.avatar.transform);
            var hits = new List<AnimUse>();
            foreach (var kv in S.animByPath)
                if (kv.Key == P || kv.Key.StartsWith(P + "/", StringComparison.Ordinal)) hits.AddRange(kv.Value);
            if (hits.Count > 0)
            {
                string clips = AstUtil.Names(hits.Select(a => a.clip), 3);
                int toggles = hits.Count(a => a.prop == "m_IsActive");
                int blend = hits.Count(a => a.prop.StartsWith("blendShape.", StringComparison.Ordinal));
                int swaps = hits.Count(a => a.objectRef && a.prop.StartsWith("m_Materials", StringComparison.Ordinal));
                int enabled = hits.Count(a => a.prop == "m_Enabled");
                int other = hits.Count - toggles - blend - swaps - enabled;
                if (toggles > 0) Add(ri, Level.Warn, "A menu toggle / animation turns it on and off (clips: " + clips + "). Deleting breaks that toggle.");
                if (blend > 0) Add(ri, Level.Warn, blend + " blend shape animation(s) use it (expressions/shape keys; clips: " + clips + ").");
                if (swaps > 0) Add(ri, Level.Warn, "A material-swap animation uses it (clips: " + clips + ").");
                if (enabled > 0) Add(ri, Level.Warn, "An animation enables/disables its renderer (clips: " + clips + ").");
                if (other > 0) Add(ri, Level.Info, other + " other animated propert(ies) use it (clips: " + clips + ").");
            }

            // links stored as plain text (VRCFury and others store some object links by name/path)
            string owner;
            if (P.Contains("/") && S.stringOwner.TryGetValue(P, out owner))
                Add(ri, Level.Warn, "A " + owner + " component stores this object's path as text (a hidden link).");
            else if (go.name.Length >= 4 && S.stringOwner.TryGetValue(go.name, out owner) && owner != "Animator" && owner != "VRCAvatarDescriptor")
                Add(ri, Level.Info, "A " + owner + " component contains the text '" + go.name + "' (it may be a link to this object).");

            // components that go away with it
            var carried = go.GetComponentsInChildren<Component>(true)
                .Where(c => c != null && (c.GetType().Name.Contains("PhysBone") || c.GetType().Name.Contains("Constraint") || c.GetType().Name.Contains("Contact")))
                .ToList();
            if (carried.Count > 0)
                Add(ri, Level.Info, "It carries " + carried.Count + " PhysBone/constraint/contact component(s) that are deleted with it.");

            Finish(ri, go);
            return ri;
        }

        static void Finish(RiskInfo ri, GameObject go)
        {
            ri.reasons = ri.reasons.OrderByDescending(r => r.level).ToList();
            ri.level = ri.reasons.Count == 0 ? Level.Ok : ri.reasons.Max(r => r.level);
            switch (ri.level)
            {
                case Level.Ok:
                    ri.verdict = go.activeInHierarchy
                        ? "Nothing I can see depends on it. Looks safe to delete (test afterwards)."
                        : "It is hidden and nothing uses it. Safest thing to delete.";
                    break;
                case Level.Info: ri.verdict = "Probably fine to delete, but check the note below."; break;
                case Level.Warn: ri.verdict = "Can be deleted, but it will break the things listed below."; break;
                default: ri.verdict = "DO NOT delete: it would break the avatar."; break;
            }
        }

        public static Savings Estimate(ScanData S, IEnumerable<GameObject> gos)
        {
            var sav = new Savings();
            var rs = new HashSet<Renderer>();
            foreach (var g in gos)
            {
                if (g == null) continue;
                sav.objects++;
                foreach (var r in g.GetComponentsInChildren<Renderer>(true)) rs.Add(r);
            }
            foreach (var tx in S.textures)
                if (tx.users.Count > 0 && tx.users.All(u => u == null || rs.Contains(u))) sav.tex += tx.vram;
            foreach (var m in S.meshes)
                if (m.users.Count > 0 && m.users.All(u => u == null || rs.Contains(u))) sav.mesh += m.bytes;
            foreach (var r in rs)
            {
                sav.tris += AstScan.CountTris(AstScan.MeshOf(r));
                sav.slots += r.sharedMaterials.Length;
            }
            return sav;
        }
    }

    // ===================================================================== preflight checks
    internal static class AstPreflight
    {
        static void Add(ScanData S, Level l, string title, string detail, GameObject target, int gotoTab, string fixLabel, Action fix)
        {
            var i = new Issue();
            i.level = l; i.title = title; i.detail = detail; i.target = target; i.gotoTab = gotoTab; i.fixLabel = fixLabel; i.fix = fix;
            S.issues.Add(i);
        }

        static void WalkStates(AnimatorStateMachine sm, ref int on, ref int off)
        {
            foreach (var s in sm.states)
            {
                if (s.state == null) continue;
                if (s.state.writeDefaultValues) on++; else off++;
            }
            foreach (var sub in sm.stateMachines)
                if (sub.stateMachine != null) WalkStates(sub.stateMachine, ref on, ref off);
        }

        static void Row(ScanData S, string name, string value, string rank)
        {
            S.stats.Add(new[] { name, value, rank });
        }

        public static void Run(ScanData S)
        {
            var av = S.avatar;
            var comps = S.comps;
            var renderers = av.GetComponentsInChildren<Renderer>(true);
            var smrs = av.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            S.smrCount = smrs.Length;
            S.meshRendererCount = av.GetComponentsInChildren<MeshRenderer>(true).Length;

            // ---- numbers
            S.totalTris = S.parts.Sum(p => p.tris);
            S.activeTris = S.parts.Where(p => p.go != null && p.go.activeInHierarchy).Sum(p => p.tris);
            S.slotCount = S.parts.Sum(p => p.slots);
            var boneSet = new HashSet<Transform>();
            foreach (var s in smrs) foreach (var b in s.bones) if (b != null) boneSet.Add(b);
            S.boneCount = boneSet.Count;
            int pb = comps.Count(c => c.GetType().Name == "VRCPhysBone");
            int pbc = comps.Count(c => c.GetType().Name == "VRCPhysBoneCollider");
            int contacts = comps.Count(c => c.GetType().Name == "VRCContactReceiver" || c.GetType().Name == "VRCContactSender");
            int constraints = comps.Count(c => c.GetType().Name.EndsWith("Constraint", StringComparison.Ordinal));
            int animators = comps.Count(c => c is Animator);
            int lights = av.GetComponentsInChildren<Light>(true).Length;
            int particles = av.GetComponentsInChildren<ParticleSystem>(true).Length;
            int audioSources = av.GetComponentsInChildren<AudioSource>(true).Length;
            double texMB = S.texBytes / 1048576.0;

            string rTris = AstUtil.Rank(S.totalTris, 32000, 70000, 70000, 70000);
            string rTex = AstUtil.Rank(texMB, 40, 75, 110, 150);
            string rSmr = AstUtil.Rank(S.smrCount, 1, 2, 8, 16);
            string rMr = AstUtil.Rank(S.meshRendererCount, 4, 8, 16, 24);
            string rSlots = AstUtil.Rank(S.slotCount, 4, 8, 16, 32);
            string rBones = AstUtil.Rank(S.boneCount, 75, 150, 256, 400);
            string rPb = AstUtil.Rank(pb, 4, 8, 16, 32);
            string rPbc = AstUtil.Rank(pbc, 4, 8, 16, 32);
            string rCon = AstUtil.Rank(contacts, 8, 16, 24, 32);
            string rCons = AstUtil.Rank(constraints, 100, 250, 300, 350);
            string rAnim = AstUtil.Rank(animators, 1, 4, 16, 32);
            string rLight = AstUtil.Rank(lights, 0, 0, 0, 1);
            string rPart = AstUtil.Rank(particles, 0, 4, 8, 16);
            string rAud = AstUtil.Rank(audioSources, 1, 4, 8, 8);

            Row(S, "Triangles (all, incl. hidden)", S.totalTris.ToString("N0") + "   (active: " + S.activeTris.ToString("N0") + ")", rTris);
            Row(S, "Texture memory (approx)", AstUtil.MB(S.texBytes), rTex);
            Row(S, "Skinned meshes", S.smrCount.ToString(), rSmr);
            Row(S, "Basic meshes", S.meshRendererCount.ToString(), rMr);
            Row(S, "Material slots", S.slotCount.ToString(), rSlots);
            Row(S, "Bones used by meshes", S.boneCount.ToString(), rBones);
            Row(S, "PhysBones / colliders", pb + " / " + pbc, AstUtil.Rank(Math.Max(pb, pbc), 4, 8, 16, 32));
            Row(S, "Contacts", contacts.ToString(), rCon);
            Row(S, "Constraints", constraints.ToString(), rCons);
            Row(S, "Animators / lights / particle systems", animators + " / " + lights + " / " + particles, "");
            Row(S, "Audio sources", audioSources.ToString(), rAud);
            long est = AstSize.Memory(S);
            Row(S, "Estimated download size", AstUtil.MB(AstSize.Download(S)), "PC limit 200 MB / Android 10 MB");
            Row(S, "Estimated memory (VRAM + RAM)", AstUtil.MB(est), "PC limit 500 MB / Android 40 MB");

            if (S.totalTris > 70000)
                Add(S, Level.Danger, "Triangles are over 70,000", "Per VRChat docs the avatar is 'Very Poor' above 70,000 triangles (hidden meshes count too).", null, 1, null, null);
            if (texMB > 150) Add(S, Level.Warn, "Texture memory is over 150 MB (Very Poor)", "Shrink big textures in the Textures tab.", null, 3, null, null);
            if (S.smrCount > 16) Add(S, Level.Warn, "More than 16 skinned meshes (Very Poor)", "Merge meshes (d4rkAvatarOptimizer does this on upload).", null, -1, null, null);
            if (S.meshRendererCount > 24) Add(S, Level.Warn, "More than 24 basic meshes (Very Poor)", "", null, -1, null, null);
            if (S.slotCount > 32) Add(S, Level.Warn, "More than 32 material slots (Very Poor)", "Merge materials (d4rkAvatarOptimizer helps).", null, -1, null, null);
            if (S.boneCount > 256) Add(S, Level.Warn, "More than 256 bones are used by meshes", "Above the Medium limit.", null, -1, null, null);
            if (AstSize.Download(S) > 10L * 1048576) Add(S, Level.Info, "Estimated download is above the Android limit (10 MB)", "Only matters if you also upload for Android/Quest. Smart Plan can aim for a smaller size.", null, 2, null, null);
            if (est > 40L * 1048576) Add(S, Level.Info, "Estimated size is above the Android limit (40 MB uncompressed)", "Only matters if you also build for Android/Quest (limits: 10 MB download, 40 MB uncompressed). PC limits are 200 MB / 500 MB.", null, -1, null, null);

            // ---- descriptor
            var descs = comps.Where(c => c.GetType().Name == "VRCAvatarDescriptor").ToList();
            if (descs.Count == 0)
                Add(S, Level.Danger, "No VRC Avatar Descriptor found", "The selected object is probably not the avatar root, or the VRChat SDK is missing.", null, -1, null, null);
            else if (descs.Count > 1)
                Add(S, Level.Danger, "More than one VRC Avatar Descriptor", "Only one per avatar. Remove the extra ones.", descs[1].gameObject, -1, null, null);
            else if (descs[0].gameObject != av)
                Add(S, Level.Warn, "The descriptor is not on the object you selected", "You selected '" + av.name + "' but the descriptor is on '" + descs[0].gameObject.name + "'. Select the object that has the descriptor.", descs[0].gameObject, -1, null, null);

            // ---- broken things
            int shown = 0;
            foreach (var tr in av.GetComponentsInChildren<Transform>(true))
            {
                var g = tr.gameObject;
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(g) > 0 && shown++ < 15)
                {
                    var captured = g;
                    Add(S, Level.Danger, "Missing script (broken component) on '" + g.name + "'", "A script was deleted or its package is missing.", g, -1, "Remove broken component",
                        () => AstOps.RemoveMissingScripts(captured));
                }
            }
            if (shown > 15) Add(S, Level.Danger, (shown - 15) + " more objects with missing scripts", "", null, -1, null, null);

            var emptySlot = new List<string>();
            var pink = new List<string>();
            var nullMesh = new List<string>();
            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) emptySlot.Add(r.name);
                    else if (mats[i].shader == null || mats[i].shader.name == "Hidden/InternalErrorShader") pink.Add(r.name + " / " + mats[i].name);
                }
                if ((r is SkinnedMeshRenderer || r is MeshRenderer) && AstScan.MeshOf(r) == null) nullMesh.Add(r.name);
            }
            if (pink.Count > 0) Add(S, Level.Danger, pink.Count + " material(s) with a missing/broken shader (will look pink)", AstUtil.Names(pink, 6), null, -1, null, null);
            if (emptySlot.Count > 0) Add(S, Level.Warn, emptySlot.Count + " empty material slot(s)", "On: " + AstUtil.Names(emptySlot, 6), null, -1, null, null);
            if (nullMesh.Count > 0) Add(S, Level.Warn, nullMesh.Count + " renderer(s) without a mesh", AstUtil.Names(nullMesh, 6), null, -1, null, null);

            var notReadable = S.meshes.Where(m => !m.readable).ToList();
            if (notReadable.Count > 0)
            {
                var toFix = notReadable.Select(m => m.mesh).ToList();
                Add(S, Level.Danger, notReadable.Count + " mesh(es) with Read/Write turned OFF",
                    "Per VRChat docs this makes the rank 'Very Poor' and the SDK asks you to fix it before upload. Meshes: " + AstUtil.Names(notReadable.Select(m => m.mesh.name), 6),
                    null, -1, "Turn Read/Write on", () => AstOps.EnableReadable(toFix));
            }

            // ---- animator / parameters
            foreach (var lp in S.layerProblems)
                Add(S, Level.Warn, "Animation layer '" + lp + "' is custom but has no controller", "Assign a controller or set it back to default in the Avatar Descriptor.", descs.Count > 0 ? descs[0].gameObject : null, -1, null, null);

            foreach (var ac in S.fxControllers)
            {
                int on = 0, off = 0;
                foreach (var layer in ac.layers)
                    if (layer.stateMachine != null) WalkStates(layer.stateMachine, ref on, ref off);
                if (on > 0 && off > 0)
                    Add(S, Level.Warn, "FX controller mixes Write Defaults (" + on + " ON / " + off + " OFF)",
                        "Mixed Write Defaults is a classic cause of weird poses and toggles that fight each other. Pick one style. (VRCFury avatars usually handle this automatically.)", null, -1, null, null);
            }

            try
            {
                if (S.descriptor != null)
                {
                    var so = new SerializedObject(S.descriptor);
                    var p = so.FindProperty("expressionParameters");
                    var obj = p != null ? p.objectReferenceValue : null;
                    if (obj != null)
                    {
                        var m = obj.GetType().GetMethod("CalcTotalCost");
                        if (m != null)
                        {
                            int cost = Convert.ToInt32(m.Invoke(obj, null));
                            if (cost > 256) Add(S, Level.Danger, "Expression parameters cost " + cost + "/256 bits", "Over the limit. Remove synced parameters.", null, -1, null, null);
                            else if (cost > 230) Add(S, Level.Warn, "Expression parameters cost " + cost + "/256 bits", "Almost full; adding toggles may hit the limit.", null, -1, null, null);
                        }
                    }
                }
            }
            catch (Exception) { }

            // ---- hierarchy problems
            var dupes = new List<string>();
            foreach (var tr in av.GetComponentsInChildren<Transform>(true))
            {
                var names = new HashSet<string>();
                foreach (Transform ch in tr)
                    if (!names.Add(ch.name)) dupes.Add(AstUtil.PathTo(ch, av.transform));
            }
            if (dupes.Count > 0)
                Add(S, Level.Warn, dupes.Count + " object(s) share a name with a sibling",
                    "Animations find objects by name/path, so duplicates can make a toggle control the wrong object. Examples: " + AstUtil.Names(dupes, 5), null, -1, null, null);

            var valid = new HashSet<string>(av.GetComponentsInChildren<Transform>(true).Select(x => AstUtil.PathTo(x, av.transform)));
            var missingPaths = S.animUses.Where(a => a.path != "" && !a.fromComponent && !valid.Contains(a.path)).Select(a => a.path).Distinct().ToList();
            if (missingPaths.Count > 0)
            {
                bool tools = S.hasVRCFury || S.hasModularAvatar;
                Add(S, tools ? Level.Info : Level.Warn, missingPaths.Count + " animated path(s) do not exist on the avatar",
                    "Animations point to objects that are missing (renamed/deleted). " + (tools ? "VRCFury/Modular Avatar create some objects only at build time, so this may be normal. " : "") + "Examples: " + AstUtil.Names(missingPaths, 4), null, -1, null, null);
            }

            // ---- other tools
            int optimizers = comps.Count(c => c.GetType().Name == "d4rkAvatarOptimizer" && c is Behaviour && ((Behaviour)c).enabled && c.gameObject.activeInHierarchy);
            if (optimizers > 1) Add(S, Level.Danger, "More than one active d4rkAvatarOptimizer component", "Its upload hook refuses to run and fails the build. Keep only one.", null, -1, null, null);
            if (S.hasVRCFury || S.hasModularAvatar)
                Add(S, Level.Info, "This avatar uses " + (S.hasVRCFury ? "VRCFury " : "") + (S.hasModularAvatar ? "Modular Avatar" : ""),
                    "These tools add things at build time that this scan cannot see (some links to objects are stored by name). After deleting or slimming anything, run Build & Test.", null, -1, null, null);

            // ---- transform / bounds
            var sc = av.transform.localScale;
            if (Mathf.Abs(sc.x - 1) > 0.001f || Mathf.Abs(sc.y - 1) > 0.001f || Mathf.Abs(sc.z - 1) > 0.001f)
                Add(S, Level.Warn, "Avatar root scale is not 1,1,1", "Scale " + sc + ". This can cause size/PhysBone/camera surprises.", av, -1, null, null);
            if (av.transform.position.sqrMagnitude > 0.0001f)
                Add(S, Level.Info, "Avatar root is not at the origin", "Position " + av.transform.position + ". Usually it should be at 0,0,0.", av, -1, null, null);
            bool haveBounds = false;
            var bounds = new Bounds();
            foreach (var r in renderers)
            {
                if (r is ParticleSystemRenderer) continue;
                if (!haveBounds) { bounds = r.bounds; haveBounds = true; } else bounds.Encapsulate(r.bounds);
            }
            if (haveBounds && (bounds.size.x > 5f || bounds.size.y > 6f || bounds.size.z > 5f))
                Add(S, Level.Info, "Avatar bounds are larger than 5 x 6 x 5 m", "Size " + bounds.size.ToString("F1") + ". Affects the performance rank (it does not block upload). Often caused by a far-away or mis-scaled mesh.", null, -1, null, null);

            // ---- hidden objects / blend shapes / audio / textures
            int hidden = S.parts.Count(p => p.go != null && !p.go.activeInHierarchy && p.renderer != null);
            if (hidden > 0)
                Add(S, Level.Info, hidden + " hidden object(s) with meshes", "Hidden objects still count for the rank and the size. Delete the ones nothing uses (Parts tab, filter 'Looks safe').", null, 1, null, null);
            int totalShapes = S.meshes.Sum(m => m.blendCount);
            if (totalShapes > 150)
                Add(S, Level.Info, totalShapes + " blend shapes on the avatar's meshes", "Unused blend shapes add to the file size. The Optimize tab finds the ones nothing uses.", null, 2, null, null);
            if (S.audioBytes > 2 * 1048576L)
                Add(S, Level.Info, "Audio clips use " + AstUtil.MB(S.audioBytes), AstUtil.Names(S.audioNames, 4), null, -1, null, null);

            int big = S.textures.Count(r => Mathf.Max(r.w, r.h) >= 4096);
            int unc = S.textures.Count(r => r.uncompressed);
            int hq = S.textures.Count(r => r.hq);
            int crunch = S.textures.Count(r => r.crunch);
            if (big > 0) Add(S, Level.Warn, big + " texture(s) at 4096 or bigger", "The biggest memory users. See the Textures tab.", null, 3, null, null);
            if (unc > 0) Add(S, Level.Warn, unc + " texture(s) are uncompressed", "Uncompressed textures use about 4x more memory. The Textures tab can fix them.", null, 3, null, null);
            if (hq > 0) Add(S, Level.Info, hq + " texture(s) use High Quality compression", "VRChat notes that High Quality compression makes the avatar bigger than Normal Quality.", null, 3, null, null);
            if (crunch > 0) Add(S, Level.Info, crunch + " texture(s) use Crunch compression", "Smaller download, but d4rkAvatarOptimizer cannot merge materials that use crunched textures, and quality drops a little.", null, -1, null, null);

            int safe = S.parts.Count(p => p.renderer != null && AstRisk.Evaluate(S, p.go).level <= Level.Info);
            int dng = S.parts.Count(p => p.renderer != null && AstRisk.Evaluate(S, p.go).level == Level.Danger);
            Add(S, Level.Info, "Parts summary: " + safe + " look safe to delete, " + dng + " are dangerous to delete", "Details in the Parts tab.", null, 1, null, null);

            S.issues = S.issues.OrderByDescending(i => i.level).ToList();
        }
    }
}

namespace AvatarSlimmer3
{
    internal class BsShape
    {
        public int index;
        public string name;
        public int frames;
        public long bytes;
        public bool keep;
        public string why = "";
    }

    internal class BsMesh
    {
        public Mesh mesh;
        public string assetPath = "";
        public List<SkinnedMeshRenderer> smrs = new List<SkinnedMeshRenderer>();
        public List<BsShape> shapes = new List<BsShape>();
        public bool enabled = true;
        public bool expanded;
        public string skipWhy;
        public int RemoveCount { get { return shapes.Count(s => !s.keep); } }
        public long RemoveBytes { get { return shapes.Where(s => !s.keep).Sum(s => s.bytes); } }
        public long TotalBytes { get { return shapes.Sum(s => s.bytes); } }
    }

    internal class BsResult
    {
        public int meshes, shapes;
        public long saved;
        public List<string> messages = new List<string>();
    }

    internal static class AstBlend
    {
        static bool NZ(Vector3 v) { return v.x != 0f || v.y != 0f || v.z != 0f; }

        static bool HasNonAscii(string s)
        {
            foreach (char c in s) if (c > 127) return true;
            return false;
        }

        static long ShapeBytes(Mesh m, int shape, Vector3[] dv, Vector3[] dn, Vector3[] dt)
        {
            long bytes = 0;
            int frames = m.GetBlendShapeFrameCount(shape);
            for (int f = 0; f < frames; f++)
            {
                m.GetBlendShapeFrameVertices(shape, f, dv, dn, dt);
                int nz = 0;
                for (int i = 0; i < dv.Length; i++)
                    if (NZ(dv[i]) || NZ(dn[i]) || NZ(dt[i])) nz++;
                bytes += nz * 40L;   // Unity stores 40 bytes per changed vertex per frame
            }
            return bytes;
        }

        public static string[] KeepList()
        {
            return AstSettings.KeepShapes.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToArray();
        }

        // ------------------------------------------------------------------ analysis
        public static List<BsMesh> Analyze(ScanData S, Action<string, float> progress)
        {
            var result = new List<BsMesh>();

            // names animated per renderer path
            var animNames = new Dictionary<string, Dictionary<string, string>>();
            foreach (var u in S.animUses)
            {
                if (u.fromComponent || u.type != "SkinnedMeshRenderer" || !u.prop.StartsWith("blendShape.", StringComparison.Ordinal)) continue;
                Dictionary<string, string> d;
                if (!animNames.TryGetValue(u.path, out d)) { d = new Dictionary<string, string>(); animNames[u.path] = d; }
                string nm = u.prop.Substring("blendShape.".Length);
                if (!d.ContainsKey(nm)) d[nm] = u.clip;
            }

            // eyelid blend shapes are stored by INDEX in the descriptor
            SkinnedMeshRenderer eyelidSmr = null;
            var eyelidIdx = new List<int>();
            if (S.descriptor != null)
            {
                try
                {
                    var so = new SerializedObject(S.descriptor);
                    var mp = so.FindProperty("customEyeLookSettings.eyelidsSkinnedMesh");
                    var arr = so.FindProperty("customEyeLookSettings.eyelidsBlendshapes");
                    if (mp != null) eyelidSmr = mp.objectReferenceValue as SkinnedMeshRenderer;
                    if (arr != null && arr.isArray)
                        for (int i = 0; i < arr.arraySize; i++) eyelidIdx.Add(arr.GetArrayElementAtIndex(i).intValue);
                }
                catch (Exception) { }
            }

            var keepList = KeepList();
            bool protVrc = AstSettings.ProtectVrcShapes;
            bool protMmd = AstSettings.ProtectMmdShapes;

            int done = 0;
            var meshes = S.meshes.Where(m => m.blendCount > 0 && m.smrs.Count > 0).ToList();
            foreach (var mi in meshes)
            {
                done++;
                if (progress != null) progress("Analysing blend shapes: " + mi.mesh.name, (float)done / Mathf.Max(1, meshes.Count));
                var bs = new BsMesh();
                bs.mesh = mi.mesh;
                bs.assetPath = mi.assetPath;
                bs.smrs = mi.smrs.Where(s => s != null).ToList();
                result.Add(bs);
                if (!mi.mesh.isReadable) { bs.skipWhy = "Read/Write is off on this mesh (turn it on first)."; continue; }

                int vc = mi.mesh.vertexCount;
                var dv = new Vector3[vc]; var dn = new Vector3[vc]; var dt = new Vector3[vc];
                var eyelidNames = new HashSet<string>();
                if (eyelidSmr != null && bs.smrs.Contains(eyelidSmr))
                    foreach (int idx in eyelidIdx)
                        if (idx >= 0 && idx < mi.mesh.blendShapeCount) eyelidNames.Add(mi.mesh.GetBlendShapeName(idx));

                for (int i = 0; i < mi.mesh.blendShapeCount; i++)
                {
                    var sh = new BsShape();
                    sh.index = i;
                    sh.name = mi.mesh.GetBlendShapeName(i);
                    sh.frames = mi.mesh.GetBlendShapeFrameCount(i);
                    sh.bytes = ShapeBytes(mi.mesh, i, dv, dn, dt);
                    sh.why = WhyKeep(S, bs, sh, animNames, eyelidNames, keepList, protVrc, protMmd);
                    sh.keep = sh.why.Length > 0;
                    bs.shapes.Add(sh);
                }
            }
            return result;
        }

        static string WhyKeep(ScanData S, BsMesh bs, BsShape sh, Dictionary<string, Dictionary<string, string>> animNames,
                              HashSet<string> eyelidNames, string[] keepList, bool protVrc, bool protMmd)
        {
            if (eyelidNames.Contains(sh.name)) return "used for eye blinking (Avatar Descriptor)";
            foreach (var smr in bs.smrs)
            {
                string path = AstUtil.PathTo(smr.transform, S.avatar.transform);
                Dictionary<string, string> d;
                string clip;
                if (animNames.TryGetValue(path, out d) && d.TryGetValue(sh.name, out clip)) return "animated (clip: " + clip + ")";
            }
            if (S.extraBlendNames.Contains(sh.name)) return "animated by an animation from a component";
            string owner;
            if (S.stringOwner.TryGetValue(sh.name, out owner))
                return owner == "VRCAvatarDescriptor" ? "used by the Avatar Descriptor (lip sync / face tracking)" : "named in a " + owner + " component";
            foreach (var smr in bs.smrs)
                if (Mathf.Abs(smr.GetBlendShapeWeight(sh.index)) > 0f) return "has a non-zero weight right now";
            if (protVrc && sh.name.StartsWith("vrc.", StringComparison.OrdinalIgnoreCase)) return "VRChat viseme/eyelid name";
            if (protMmd && HasNonAscii(sh.name)) return "MMD-style name (kept for MMD compatibility)";
            string lower = sh.name.ToLowerInvariant();
            foreach (var k in keepList) if (lower.Contains(k)) return "in your keep list";
            return "";
        }

        // ------------------------------------------------------------------ apply
        public static BsResult Apply(ScanData S, List<BsMesh> plan, Action<string, float> progress)
        {
            var res = new BsResult();
            string folder = AstUtil.Root + "/Meshes/" + AstUtil.SafeName(S.avatar.name);
            AstUtil.EnsureFolder(folder);
            var todo = plan.Where(b => b.enabled && b.skipWhy == null && b.mesh != null && b.RemoveCount > 0).ToList();
            int done = 0;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Remove unused blend shapes");
            int group = Undo.GetCurrentGroup();
            foreach (var bs in todo)
            {
                done++;
                if (progress != null) progress("Building slim mesh: " + bs.mesh.name, (float)done / Mathf.Max(1, todo.Count));
                try
                {
                    var src = bs.mesh;
                    int vc = src.vertexCount;
                    var n = UnityEngine.Object.Instantiate(src);
                    n.name = src.name + "_slim";
                    n.ClearBlendShapes();
                    var dv = new Vector3[vc]; var dn = new Vector3[vc]; var dt = new Vector3[vc];
                    var oldToNew = new int[src.blendShapeCount];
                    for (int i = 0; i < oldToNew.Length; i++) oldToNew[i] = -1;
                    int ni = 0;
                    foreach (var sh in bs.shapes)
                    {
                        if (!sh.keep) continue;
                        for (int f = 0; f < sh.frames; f++)
                        {
                            src.GetBlendShapeFrameVertices(sh.index, f, dv, dn, dt);
                            n.AddBlendShapeFrame(sh.name, src.GetBlendShapeFrameWeight(sh.index, f), dv, dn, dt);
                        }
                        oldToNew[sh.index] = ni++;
                    }
                    string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + AstUtil.SafeName(src.name) + "_slim.asset");
                    AssetDatabase.CreateAsset(n, path);
                    var created = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                    if (created != null) n = created;
                    if (!n.isReadable) res.messages.Add("Warning: '" + n.name + "' is not readable; turn Read/Write on before uploading.");

                    foreach (var smr in bs.smrs)
                    {
                        if (smr == null) continue;
                        var old = new float[src.blendShapeCount];
                        for (int i = 0; i < old.Length; i++) old[i] = smr.GetBlendShapeWeight(i);
                        Undo.RecordObject(smr, "Remove unused blend shapes");
                        smr.sharedMesh = n;
                        for (int i = 0; i < old.Length; i++)
                            if (oldToNew[i] >= 0 && old[i] != 0f) smr.SetBlendShapeWeight(oldToNew[i], old[i]);
                        EditorUtility.SetDirty(smr);
                        PrefabUtility.RecordPrefabInstancePropertyModifications(smr);
                    }
                    RemapEyelids(S, bs, oldToNew);
                    long before = AstUtil.MemOf(src);
                    long after = AstUtil.MemOf(n);
                    res.meshes++;
                    res.shapes += bs.RemoveCount;
                    res.saved += Math.Max(0, before - after);
                    AstLog.Add("Blend shapes: '" + src.name + "' " + src.blendShapeCount + " -> " + n.blendShapeCount + " shapes, saved about " + AstUtil.MB(Math.Max(0, before - after)) + " (new mesh: " + path + ")");
                }
                catch (Exception e)
                {
                    res.messages.Add("Failed on '" + (bs.mesh != null ? bs.mesh.name : "?") + "': " + e.Message);
                    Debug.LogException(e);
                }
            }
            Undo.CollapseUndoOperations(group);
            AssetDatabase.SaveAssets();
            return res;
        }

        // the descriptor stores eyelid blend shapes by index, so indices must follow the new mesh
        static void RemapEyelids(ScanData S, BsMesh bs, int[] oldToNew)
        {
            if (S.descriptor == null) return;
            try
            {
                var so = new SerializedObject(S.descriptor);
                var mp = so.FindProperty("customEyeLookSettings.eyelidsSkinnedMesh");
                var arr = so.FindProperty("customEyeLookSettings.eyelidsBlendshapes");
                if (mp == null || arr == null || !arr.isArray) return;
                var smr = mp.objectReferenceValue as SkinnedMeshRenderer;
                if (smr == null || !bs.smrs.Contains(smr)) return;
                bool changed = false;
                for (int i = 0; i < arr.arraySize; i++)
                {
                    var el = arr.GetArrayElementAtIndex(i);
                    int oldIdx = el.intValue;
                    if (oldIdx >= 0 && oldIdx < oldToNew.Length && oldToNew[oldIdx] >= 0 && oldToNew[oldIdx] != oldIdx)
                    {
                        el.intValue = oldToNew[oldIdx];
                        changed = true;
                    }
                }
                if (changed) so.ApplyModifiedProperties();
            }
            catch (Exception) { }
        }
    }
}

namespace AvatarSlimmer3
{
    // ===================================================================== small fixes
    internal static class AstOps
    {
        public static void RemoveMissingScripts(GameObject g)
        {
            if (g == null) return;
            try
            {
                Undo.RegisterFullObjectHierarchyUndo(g, "Remove broken components");
                int n = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(g);
                AstLog.Add("Removed " + n + " broken component(s) from '" + g.name + "'");
            }
            catch (Exception e)
            {
                AstLog.Add("Could not remove broken components on '" + g.name + "': " + e.Message + " (it may belong to a prefab; unpack it first)");
            }
        }

        public static void EnableReadable(List<Mesh> meshes)
        {
            var paths = new HashSet<string>();
            int skipped = 0;
            foreach (var m in meshes)
            {
                if (m == null) continue;
                string p = AssetDatabase.GetAssetPath(m);
                var imp = string.IsNullOrEmpty(p) ? null : AssetImporter.GetAtPath(p) as ModelImporter;
                if (imp == null) { skipped++; continue; }
                paths.Add(p);
            }
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var p in paths)
                {
                    var imp = AssetImporter.GetAtPath(p) as ModelImporter;
                    if (imp == null) continue;
                    imp.isReadable = true;
                    imp.SaveAndReimport();
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.Refresh();
            }
            AstLog.Add("Read/Write turned on for " + paths.Count + " model file(s)" + (skipped > 0 ? " (" + skipped + " mesh(es) are not part of a model file and were skipped)" : ""));
        }
    }

    // ===================================================================== scene backups
    internal static class AstBackup
    {
        public static string Folder { get { return AstUtil.Root + "/Backups"; } }

        static string BKey(string sceneName) { return "AvatarSlimmer2.baseline." + sceneName; }
        public static string GetBaseline(string sceneName) { return EditorPrefs.GetString(BKey(sceneName), ""); }
        public static void SetBaseline(string sceneName, string path) { EditorPrefs.SetString(BKey(sceneName), path); }

        public static string Create(GameObject avatar, string reason)
        {
            try
            {
                Scene scene = avatar != null ? avatar.scene : SceneManager.GetActiveScene();
                if (!scene.IsValid()) return null;
                AstUtil.EnsureFolder(Folder);
                string name = AstUtil.SafeName(string.IsNullOrEmpty(scene.name) ? "Untitled" : scene.name);
                string path = AssetDatabase.GenerateUniqueAssetPath(Folder + "/" + name + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".unity");
                bool ok = EditorSceneManager.SaveScene(scene, path, true);
                if (!ok) return null;
                AssetDatabase.ImportAsset(path);
                // the first backup of a scene is pinned as its "original state" (never pruned, used by Restore everything)
                string bl = GetBaseline(name);
                if (string.IsNullOrEmpty(bl) || !File.Exists(AstTex.ProjectPathOf(bl))) SetBaseline(name, path);
                Prune(name);
                AstLog.Add("Scene backup saved: " + path + "  (" + reason + ")");
                return path;
            }
            catch (Exception e)
            {
                AstLog.Add("Backup failed: " + e.Message);
                return null;
            }
        }

        public static List<string> List()
        {
            var res = new List<string>();
            if (!AssetDatabase.IsValidFolder(Folder)) return res;
            foreach (var guid in AssetDatabase.FindAssets("t:Scene", new[] { Folder }))
                res.Add(AssetDatabase.GUIDToAssetPath(guid));
            res.Sort(StringComparer.Ordinal);
            res.Reverse();
            return res;
        }

        static void Prune(string sceneName)
        {
            var mine = List().Where(p => Path.GetFileName(p).StartsWith(sceneName + "_", StringComparison.Ordinal)).ToList();
            int keep = AstSettings.BackupKeep;
            string pinned = GetBaseline(sceneName);
            for (int i = keep; i < mine.Count; i++)
                if (mine[i] != pinned) AssetDatabase.DeleteAsset(mine[i]);
        }

        public static void Open(string path)
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            EditorSceneManager.OpenScene(path);
        }
    }

    // ===================================================================== hide / delete
    internal static class AstDelete
    {
        public static int Hide(IEnumerable<GameObject> gos)
        {
            int n = 0;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Avatar Slimmer hide");
            int group = Undo.GetCurrentGroup();
            foreach (var g in gos)
            {
                if (g == null || !g.activeSelf) continue;
                Undo.RecordObject(g, "Hide");
                g.SetActive(false);
                n++;
            }
            Undo.CollapseUndoOperations(group);
            AstLog.Add("Hid " + n + " object(s)");
            return n;
        }

        public static int Show(IEnumerable<GameObject> gos)
        {
            int n = 0;
            foreach (var g in gos)
            {
                if (g == null || g.activeSelf) continue;
                Undo.RecordObject(g, "Show");
                g.SetActive(true);
                n++;
            }
            return n;
        }

        // keeps only the top-most objects (a child is removed anyway when its parent is deleted)
        public static List<GameObject> TopMost(IEnumerable<GameObject> gos)
        {
            var list = gos.Where(g => g != null).Distinct().ToList();
            return list.Where(g => !list.Any(o => o != g && g.transform.IsChildOf(o.transform))).ToList();
        }

        public static int CountLocked(IEnumerable<GameObject> gos)
        {
            return TopMost(gos).Count(g => AstUtil.LockedByPrefab(g));
        }

        // returns how many were deleted; skipped = objects that could not be deleted
        public static int Delete(ScanData S, IEnumerable<GameObject> gos, bool unpackIfNeeded, out int skipped)
        {
            skipped = 0;
            var targets = TopMost(gos).Where(g => g != S.avatar).ToList();
            if (targets.Count == 0) return 0;
            if (AstSettings.AutoBackup) AstBackup.Create(S.avatar, "before delete");

            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Avatar Slimmer delete");
            int group = Undo.GetCurrentGroup();
            if (unpackIfNeeded)
            {
                var roots = new HashSet<GameObject>();
                foreach (var g in targets)
                    if (AstUtil.LockedByPrefab(g))
                    {
                        var r = PrefabUtility.GetOutermostPrefabInstanceRoot(g);
                        if (r != null) roots.Add(r);
                    }
                foreach (var r in roots)
                {
                    AstLog.Add("Unpacking prefab instance '" + r.name + "' so its parts can be deleted");
                    PrefabUtility.UnpackPrefabInstance(r, PrefabUnpackMode.Completely, InteractionMode.UserAction);
                }
            }
            int deleted = 0;
            foreach (var g in targets)
            {
                if (g == null) continue;
                if (AstUtil.LockedByPrefab(g)) { skipped++; continue; }
                string nm = g.name;
                Undo.DestroyObjectImmediate(g);
                deleted++;
                AstLog.Add("Deleted '" + nm + "'");
            }
            Undo.CollapseUndoOperations(group);
            return deleted;
        }
    }

    // ===================================================================== add clothing
    internal class OutfitInfo
    {
        public GameObject source;
        public int renderers, bonesNeeded;
        public List<string> mapped = new List<string>();
        public List<string> unmapped = new List<string>();
        public List<string> warnings = new List<string>();
        public bool usedHumanoid;
        public bool CanApply { get { return renderers > 0 && mapped.Count > 0; } }
    }

    internal static class AstOutfit
    {
        static readonly Regex Suffix = new Regex(@"\.\d{3}$|_\d{3}$", RegexOptions.Compiled);

        public static string Norm(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int c = name.LastIndexOf(':');
            if (c >= 0) name = name.Substring(c + 1);
            name = Suffix.Replace(name, "");
            var sb = new System.Text.StringBuilder();
            foreach (char ch in name)
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString();
        }

        static Transform ChooseBest(Transform t, List<Transform> cands, Dictionary<Transform, Transform> map)
        {
            Transform parentMapped;
            if (t.parent != null && map.TryGetValue(t.parent, out parentMapped))
            {
                var same = cands.FirstOrDefault(c => c.parent == parentMapped);
                if (same != null) return same;
            }
            Func<Transform, int> depth = x => { int d = 0; while (x.parent != null) { d++; x = x.parent; } return d; };
            return cands.OrderBy(depth).First();
        }

        // maps outfit bones -> avatar bones (by humanoid role first, then by name)
        public static Dictionary<Transform, Transform> BuildMap(GameObject avatar, GameObject outfit, bool useHumanoid, OutfitInfo info)
        {
            var map = new Dictionary<Transform, Transform>();
            var outRoot = outfit.transform;
            var avTs = avatar.GetComponentsInChildren<Transform>(true).Where(t => !t.IsChildOf(outRoot) && t != avatar.transform).ToList();

            if (useHumanoid)
            {
                var oa = outfit.GetComponent<Animator>();
                if (oa == null) oa = outfit.GetComponentInChildren<Animator>(true);
                var aa = avatar.GetComponent<Animator>();
                if (oa != null && oa.isHuman && aa != null && aa.isHuman)
                {
                    foreach (HumanBodyBones b in Enum.GetValues(typeof(HumanBodyBones)))
                    {
                        if (b == HumanBodyBones.LastBone) continue;
                        var ot = oa.GetBoneTransform(b);
                        var at = aa.GetBoneTransform(b);
                        if (ot != null && at != null && ot.IsChildOf(outRoot)) { map[ot] = at; if (info != null) info.usedHumanoid = true; }
                    }
                }
            }

            var byName = new Dictionary<string, List<Transform>>();
            foreach (var t in avTs)
            {
                string k = Norm(t.name);
                if (k.Length == 0) continue;
                List<Transform> l;
                if (!byName.TryGetValue(k, out l)) { l = new List<Transform>(); byName[k] = l; }
                l.Add(t);
            }

            var needed = new List<Transform>();
            var seen = new HashSet<Transform>();
            foreach (var smr in outfit.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var all = new List<Transform>(smr.bones);
                if (smr.rootBone != null) all.Add(smr.rootBone);
                foreach (var b in all)
                {
                    var t = b;
                    while (t != null && t != outRoot)
                    {
                        if (seen.Add(t)) needed.Add(t);
                        t = t.parent;
                    }
                }
            }
            // parents before children so ChooseBest can use the parent's mapping
            needed = needed.OrderBy(t => { int d = 0; var x = t; while (x.parent != null) { d++; x = x.parent; } return d; }).ToList();
            foreach (var t in needed)
            {
                if (map.ContainsKey(t)) continue;
                List<Transform> c;
                if (byName.TryGetValue(Norm(t.name), out c) && c.Count > 0)
                    map[t] = c.Count == 1 ? c[0] : ChooseBest(t, c, map);
            }
            if (info != null)
            {
                info.bonesNeeded = needed.Count;
                foreach (var t in needed)
                {
                    if (map.ContainsKey(t)) info.mapped.Add(t.name + " -> " + map[t].name);
                    else info.unmapped.Add(t.name);
                }
            }
            return map;
        }

        public static OutfitInfo Analyze(GameObject avatar, GameObject source, bool useHumanoid)
        {
            var info = new OutfitInfo();
            info.source = source;
            if (source == null) return info;
            info.renderers = source.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
            if (info.renderers == 0) { info.warnings.Add("This object has no skinned meshes (it is not clothing that follows the body)."); return info; }
            BuildMap(avatar, source, useHumanoid, info);
            if (info.mapped.Count == 0) info.warnings.Add("No bone names matched the avatar's bones. This outfit was probably made for a different armature.");
            int total = info.mapped.Count + info.unmapped.Count;
            if (total > 0 && info.unmapped.Count > total / 2) info.warnings.Add("More than half of the outfit's bones have no match; check the result carefully.");
            return info;
        }

        public static GameObject Apply(ScanData S, GameObject source, bool resetPos, bool useHumanoid, out string message)
        {
            message = "";
            var avatar = S.avatar;
            if (AstSettings.AutoBackup) AstBackup.Create(avatar, "before adding outfit");
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName("Add outfit");
            int group = Undo.GetCurrentGroup();

            GameObject inst;
            if (EditorUtility.IsPersistent(source))
            {
                inst = (GameObject)PrefabUtility.InstantiatePrefab(source, avatar.transform);
                if (inst == null) { message = "Could not create the outfit."; return null; }
                Undo.RegisterCreatedObjectUndo(inst, "Add outfit");
            }
            else
            {
                inst = source;
                Undo.SetTransformParent(inst.transform, avatar.transform, "Add outfit");
            }
            if (PrefabUtility.IsPartOfPrefabInstance(inst))
            {
                // only the outfit's own prefab root is unpacked (never the avatar's)
                var root = PrefabUtility.GetNearestPrefabInstanceRoot(inst);
                if (root == inst) PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.UserAction);
            }
            if (resetPos)
            {
                Undo.RecordObject(inst.transform, "Add outfit");
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.identity;
            }

            var info = new OutfitInfo();
            var map = BuildMap(avatar, inst, useHumanoid, info);
            if (map.Count == 0)
            {
                Undo.RevertAllDownToGroup(group);
                message = "No bones matched, so nothing was added.";
                return null;
            }

            // 1) point every mesh at the avatar's bones
            var smrs = inst.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var smr in smrs)
            {
                Undo.RecordObject(smr, "Add outfit");
                var bones = smr.bones;
                for (int i = 0; i < bones.Length; i++)
                {
                    Transform v;
                    if (bones[i] != null && map.TryGetValue(bones[i], out v)) bones[i] = v;
                }
                smr.bones = bones;
                Transform rv;
                if (smr.rootBone != null && map.TryGetValue(smr.rootBone, out rv)) smr.rootBone = rv;
                EditorUtility.SetDirty(smr);
            }

            // 2) keep bones that have no match but are used by a mesh (skirt, hair...) by moving them under the nearest matched parent
            var usedUnmapped = new HashSet<Transform>();
            foreach (var smr in smrs)
                foreach (var b in smr.bones)
                    if (b != null && b.IsChildOf(inst.transform) && !map.ContainsKey(b) && !map.ContainsValue(b)) usedUnmapped.Add(b);
            foreach (var t in inst.GetComponentsInChildren<Transform>(true))
            {
                if (map.ContainsKey(t) || t == inst.transform) continue;
                bool hasExtra = t.GetComponents<Component>().Any(c => c != null && !(c is Transform));
                if (hasExtra && !t.GetComponents<Component>().Any(c => c is Renderer || c is MeshFilter)) usedUnmapped.Add(t);
            }
            int moved = 0;
            var movedTops = new HashSet<Transform>();
            foreach (var u in usedUnmapped.ToList())
            {
                if (u == null) continue;
                Transform anc = u.parent;
                while (anc != null && anc != inst.transform && !map.ContainsKey(anc)) anc = anc.parent;
                if (anc == null || anc == inst.transform) continue;
                Transform top = u;
                while (top.parent != anc) top = top.parent;
                if (!movedTops.Add(top)) continue;
                Undo.SetTransformParent(top, map[anc], "Add outfit");
                moved++;
            }

            // 3) remove the outfit's own (now empty) armature and its Animator
            var armatureTops = new HashSet<Transform>();
            foreach (var kv in map)
            {
                var t = kv.Key;
                if (t == null || !t.IsChildOf(inst.transform) || t == inst.transform) continue;
                while (t.parent != null && t.parent != inst.transform) t = t.parent;
                armatureTops.Add(t);
            }
            int removedArm = 0;
            foreach (var top in armatureTops)
            {
                if (top == null) continue;
                if (top.GetComponentsInChildren<Renderer>(true).Length > 0) { message += "The outfit's armature object '" + top.name + "' also contains meshes, so it was left in place. "; continue; }
                if (usedUnmapped.Any(u => u != null && u.IsChildOf(top))) { message += "The outfit's armature object '" + top.name + "' still holds bones that its meshes use, so it was left in place. "; continue; }
                Undo.DestroyObjectImmediate(top.gameObject);
                removedArm++;
            }
            var anim = inst.GetComponent<Animator>();
            if (anim != null) Undo.DestroyObjectImmediate(anim);

            Undo.CollapseUndoOperations(group);
            AstLog.Add("Outfit '" + inst.name + "' added: " + info.mapped.Count + " bones matched, " + moved + " extra bone chain(s) kept, " + removedArm + " armature object(s) removed");
            message += info.mapped.Count + " bones matched. " + (info.unmapped.Count > 0 ? info.unmapped.Count + " had no match" + (moved > 0 ? " (used ones were kept)." : ".") : "");
            Selection.activeGameObject = inst;
            return inst;
        }
    }
}

namespace AvatarSlimmer3
{
    // Estimates how big the avatar is, in memory and as a download, BEFORE you build/upload.
    // The download number is an estimate (compression ratios per kind of data); you can calibrate it once with the
    // real number that the VRChat SDK shows after Build & Test, and later estimates for that avatar get much closer.
    internal static class AstSize
    {
        public const double TexRatio = 0.80, CrunchRatio = 0.35, MeshRatio = 0.50, AnimRatio = 0.30, AudioRatio = 1.0;

        static string Key(GameObject av) { return "AvatarSlimmer2.calib." + AstUtil.SafeName(av != null ? av.name : "none"); }

        public static double Factor(GameObject av)
        {
            try
            {
                double f = EditorPrefs.GetFloat(Key(av), 1f);
                return (f < 0.2 || f > 5.0) ? 1.0 : f;
            }
            catch (Exception) { return 1.0; }
        }

        public static bool IsCalibrated(GameObject av) { return EditorPrefs.HasKey(Key(av)); }
        public static void ClearCalibration(GameObject av) { EditorPrefs.DeleteKey(Key(av)); }
        public static double Ratio(bool crunch) { return crunch ? CrunchRatio : TexRatio; }

        public static double RawDownload(ScanData S)
        {
            double d = 0;
            foreach (var t in S.textures) d += t.vram * Ratio(t.crunch);
            d += S.staleBytes * TexRatio + S.meshBytes * MeshRatio + S.animBytes * AnimRatio + S.audioBytes * AudioRatio;
            return d;
        }

        public static long Download(ScanData S) { return (long)(RawDownload(S) * Factor(S.avatar)); }
        public static long Memory(ScanData S) { return S.texBytes + S.staleBytes + S.meshBytes + S.audioBytes + S.animBytes; }

        public static bool Calibrate(ScanData S, double actualMB)
        {
            double raw = RawDownload(S);
            if (raw <= 0 || actualMB <= 0) return false;
            double f = actualMB * 1048576.0 / raw;
            EditorPrefs.SetFloat(Key(S.avatar), Mathf.Clamp((float)f, 0.2f, 5f));
            return true;
        }

        // download estimate split by kind, for the bar chart
        public static List<KeyValuePair<string, double>> Breakdown(ScanData S)
        {
            double f = Factor(S.avatar);
            var l = new List<KeyValuePair<string, double>>();
            l.Add(new KeyValuePair<string, double>("Textures", S.textures.Sum(t => t.vram * Ratio(t.crunch)) * f));
            l.Add(new KeyValuePair<string, double>("Unused texture links", S.staleBytes * TexRatio * f));
            l.Add(new KeyValuePair<string, double>("Meshes (+blend shapes)", S.meshBytes * MeshRatio * f));
            l.Add(new KeyValuePair<string, double>("Animations", S.animBytes * AnimRatio * f));
            l.Add(new KeyValuePair<string, double>("Audio", S.audioBytes * AudioRatio * f));
            return l;
        }

        public static string FormatMB(double bytes) { return (bytes / 1048576.0).ToString("0.0") + " MB"; }
    }
}

namespace AvatarSlimmer3
{
    internal class StaleRef { public Material mat; public string matPath; public string prop; public Texture tex; }

    // ===================================================================== material texture links
    internal static class AstMat
    {
        public static List<Material> AvatarMaterials(ScanData S)
        {
            var set = new HashSet<Material>();
            var list = new List<Material>();
            foreach (var r in S.avatar.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                    if (m != null && set.Add(m)) list.Add(m);
            return list;
        }

        public static bool Editable(Material m)
        {
            string p = AssetDatabase.GetAssetPath(m);
            return !string.IsNullOrEmpty(p) && !p.StartsWith("Packages/", StringComparison.Ordinal);
        }

        // Textures a material still remembers for properties its CURRENT shader no longer has. They are still pulled
        // into the build, so they add size for nothing. (Typical after switching a material to another shader.)
        public static List<StaleRef> FindStale(ScanData S)
        {
            var res = new List<StaleRef>();
            foreach (var m in AvatarMaterials(S))
            {
                if (!Editable(m) || m.shader == null) continue;
                string mp = AssetDatabase.GetAssetPath(m);
                var active = new HashSet<string>(m.GetTexturePropertyNames());
                try
                {
                    var so = new SerializedObject(m);
                    var envs = so.FindProperty("m_SavedProperties.m_TexEnvs");
                    if (envs == null || !envs.isArray) continue;
                    for (int i = 0; i < envs.arraySize; i++)
                    {
                        var el = envs.GetArrayElementAtIndex(i);
                        var n = el.FindPropertyRelative("first");
                        var t = el.FindPropertyRelative("second.m_Texture");
                        if (n == null || t == null) continue;
                        var tex = t.objectReferenceValue as Texture;
                        if (tex == null || active.Contains(n.stringValue)) continue;
                        res.Add(new StaleRef { mat = m, matPath = mp, prop = n.stringValue, tex = tex });
                    }
                }
                catch (Exception) { }
            }
            return res;
        }

        // size of the stale textures that nothing else on the avatar uses
        public static long StaleBytes(ScanData S, List<StaleRef> stale)
        {
            var active = new HashSet<Texture>();
            foreach (var t in S.textures) active.Add(t.tex);
            var seen = new HashSet<Texture>();
            long b = 0;
            foreach (var s in stale)
                if (!active.Contains(s.tex) && seen.Add(s.tex)) b += AstUtil.MemOf(s.tex);
            return b;
        }

        static bool SetSlot(Material m, string prop, Texture tex)
        {
            try
            {
                var so = new SerializedObject(m);
                var envs = so.FindProperty("m_SavedProperties.m_TexEnvs");
                if (envs != null && envs.isArray)
                {
                    for (int i = 0; i < envs.arraySize; i++)
                    {
                        var el = envs.GetArrayElementAtIndex(i);
                        var n = el.FindPropertyRelative("first");
                        var t = el.FindPropertyRelative("second.m_Texture");
                        if (n != null && t != null && n.stringValue == prop)
                        {
                            t.objectReferenceValue = tex;
                            so.ApplyModifiedProperties();
                            return true;
                        }
                    }
                }
            }
            catch (Exception) { }
            if (m.HasProperty(prop)) { m.SetTexture(prop, tex); return true; }
            return false;
        }

        public static int ClearStale(List<StaleRef> list, AstChangeLog log)
        {
            int n = 0;
            foreach (var s in list)
            {
                if (s.mat == null || s.tex == null || !Editable(s.mat)) continue;
                string texPath = AssetDatabase.GetAssetPath(s.tex);
                Undo.RecordObject(s.mat, "Clear unused texture link");
                if (SetSlot(s.mat, s.prop, null))
                {
                    EditorUtility.SetDirty(s.mat);
                    if (!string.IsNullOrEmpty(texPath)) log.matRefs.Add(new AstMatLogEntry { materialPath = s.matPath, prop = s.prop, texturePath = texPath });
                    n++;
                }
            }
            return n;
        }

        public static int RestoreRefs(List<AstMatLogEntry> entries)
        {
            int n = 0;
            foreach (var e in entries)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>(e.materialPath);
                var t = AssetDatabase.LoadAssetAtPath<Texture>(e.texturePath);
                if (m == null || t == null) continue;
                Undo.RecordObject(m, "Restore texture link");
                if (SetSlot(m, e.prop, t)) { EditorUtility.SetDirty(m); n++; }
            }
            return n;
        }

        // every (material, property) on the avatar that currently holds this texture
        public static List<KeyValuePair<Material, string>> RefsTo(ScanData S, Texture tex)
        {
            var res = new List<KeyValuePair<Material, string>>();
            foreach (var m in AvatarMaterials(S))
            {
                if (m.shader == null) continue;
                foreach (var p in m.GetTexturePropertyNames())
                    if (m.GetTexture(p) == tex) res.Add(new KeyValuePair<Material, string>(m, p));
            }
            return res;
        }
    }

    // ===================================================================== duplicate textures
    internal class DupGroup
    {
        public TexRow keep;
        public List<TexRow> remove = new List<TexRow>();
        public bool blocked;          // a material that uses a duplicate cannot be edited (it lives in Packages)
        public long saveMem;
    }

    internal static class AstDup
    {
        static string HashFile(string assetPath)
        {
            try
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                using (var fs = File.OpenRead(AstTex.ProjectPathOf(assetPath)))
                    return BitConverter.ToString(md5.ComputeHash(fs));
            }
            catch (Exception) { return null; }
        }

        // the same image imported twice (copied into two folders, or from two avatars) is stored twice in the build
        public static List<DupGroup> Find(ScanData S, Action<string, float> progress)
        {
            var byKey = new Dictionary<string, List<TexRow>>();
            for (int i = 0; i < S.textures.Count; i++)
            {
                var r = S.textures[i];
                if (r.inPackages) continue;
                if (progress != null && i % 5 == 0) progress("Comparing textures " + i + "/" + S.textures.Count, (float)i / Mathf.Max(1, S.textures.Count));
                var imp = AssetImporter.GetAtPath(r.path) as TextureImporter;
                if (imp == null) continue;
                string h = HashFile(r.path);
                if (h == null) continue;
                string key = h + "|" + (int)imp.textureType + "|" + (imp.sRGBTexture ? 1 : 0) + "|" + (int)imp.alphaSource + "|" + (imp.alphaIsTransparency ? 1 : 0)
                             + "|" + (imp.mipmapEnabled ? 1 : 0) + "|" + (int)imp.wrapMode + "|" + (int)imp.filterMode + "|" + (int)imp.textureCompression
                             + "|" + imp.maxTextureSize + "|" + (imp.isReadable ? 1 : 0) + "|" + (imp.crunchedCompression ? 1 : 0);
                List<TexRow> l;
                if (!byKey.TryGetValue(key, out l)) { l = new List<TexRow>(); byKey[key] = l; }
                l.Add(r);
            }
            var res = new List<DupGroup>();
            foreach (var kv in byKey)
            {
                if (kv.Value.Count < 2) continue;
                var g = new DupGroup();
                g.keep = kv.Value.OrderByDescending(x => x.users.Count).First();
                foreach (var r in kv.Value) if (r != g.keep) g.remove.Add(r);
                foreach (var r in g.remove)
                    foreach (var pair in AstMat.RefsTo(S, r.tex))
                        if (!AstMat.Editable(pair.Key)) g.blocked = true;
                g.saveMem = g.remove.Sum(x => x.vram);
                res.Add(g);
            }
            return res.OrderByDescending(x => x.saveMem).ToList();
        }

        public static int Apply(ScanData S, List<DupGroup> groups, AstChangeLog log)
        {
            int n = 0;
            foreach (var g in groups)
            {
                if (g.blocked) continue;
                foreach (var r in g.remove)
                {
                    foreach (var pair in AstMat.RefsTo(S, r.tex))
                    {
                        var m = pair.Key;
                        Undo.RecordObject(m, "Merge duplicate textures");
                        m.SetTexture(pair.Value, g.keep.tex);
                        EditorUtility.SetDirty(m);
                        log.matRefs.Add(new AstMatLogEntry { materialPath = AssetDatabase.GetAssetPath(m), prop = pair.Value, texturePath = r.path });
                        n++;
                    }
                }
            }
            return n;
        }
    }

    // ===================================================================== audio
    internal class AudioPlan
    {
        public AudioClip clip;
        public string path;
        public long bytes;
        public long saveEst;
        public bool already;
    }

    internal static class AstAudio
    {
        public static List<AudioPlan> Find(ScanData S)
        {
            var res = new List<AudioPlan>();
            foreach (var c in S.audioClipList)
            {
                if (c == null) continue;
                string p = AssetDatabase.GetAssetPath(c);
                var imp = string.IsNullOrEmpty(p) ? null : AssetImporter.GetAtPath(p) as AudioImporter;
                if (imp == null || imp.ContainsSampleSettingsOverride("Standalone")) continue;
                var s = imp.defaultSampleSettings;
                var ap = new AudioPlan();
                ap.clip = c;
                ap.path = p;
                ap.bytes = AstUtil.MemOf(c);
                ap.already = s.compressionFormat == AudioCompressionFormat.Vorbis && s.quality <= 0.5f;
                double ratio = s.compressionFormat == AudioCompressionFormat.PCM ? 0.8 : s.compressionFormat == AudioCompressionFormat.ADPCM ? 0.5 : 0.3;
                ap.saveEst = ap.already ? 0 : (long)(ap.bytes * ratio);
                res.Add(ap);
            }
            return res;
        }

        public static int Apply(List<AudioPlan> plans, AstChangeLog log)
        {
            int n = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var p in plans)
                {
                    if (p.already) continue;
                    var imp = AssetImporter.GetAtPath(p.path) as AudioImporter;
                    if (imp == null) continue;
                    var s = imp.defaultSampleSettings;
                    log.audio.Add(new AstAudioLogEntry { path = p.path, oldFormat = (int)s.compressionFormat, oldQuality = s.quality, oldMono = imp.forceToMono });
                    s.compressionFormat = AudioCompressionFormat.Vorbis;
                    s.quality = 0.5f;
                    imp.defaultSampleSettings = s;
                    imp.SaveAndReimport();
                    n++;
                }
            }
            finally { AssetDatabase.StopAssetEditing(); AssetDatabase.Refresh(); }
            return n;
        }

        public static int Restore(List<AstAudioLogEntry> entries)
        {
            int n = 0;
            foreach (var e in entries)
            {
                var imp = AssetImporter.GetAtPath(e.path) as AudioImporter;
                if (imp == null) continue;
                var s = imp.defaultSampleSettings;
                s.compressionFormat = (AudioCompressionFormat)e.oldFormat;
                s.quality = e.oldQuality;
                imp.defaultSampleSettings = s;
                imp.SaveAndReimport();
                n++;
            }
            return n;
        }
    }

    // ===================================================================== the plan
    internal class PlanData
    {
        public bool built;
        public List<DupGroup> dups = new List<DupGroup>();
        public List<BsMesh> blend = new List<BsMesh>();
        public List<AudioPlan> audio = new List<AudioPlan>();
        public List<Part> proposals = new List<Part>();          // accessories the tool would REMOVE (only after you approve)
        public HashSet<GameObject> approved = new HashSet<GameObject>();
        public bool useStale = true, useDup = true, useFlat = true, useCompress = true, useBlend = true, useAudio = true;
        public int texLevel = 2;
    }

    internal struct Est { public double mem, dl; }

    internal class FitResult
    {
        public bool reached;
        public int level;
        public double estValue;
        public bool needsProposals;
        public double withProposals;
    }

    internal static class AstPlan
    {
        // caps per level for [Color, Normal, Mask, Emission, MatCap, Other]; 0 = no limit
        static readonly int[][] LevelCaps =
        {
            new[] { 0, 0, 0, 0, 0, 0 },
            new[] { 4096, 2048, 2048, 2048, 1024, 2048 },
            new[] { 2048, 1024, 1024, 1024, 512, 1024 },
            new[] { 1024, 512, 512, 512, 256, 512 },
            new[] { 512, 256, 256, 256, 128, 256 }
        };
        // face / eye / head textures keep more detail
        static readonly int[][] SensCaps =
        {
            new[] { 0, 0, 0, 0, 0, 0 },
            new[] { 0, 0, 0, 0, 0, 0 },
            new[] { 0, 0, 0, 0, 0, 0 },
            new[] { 2048, 1024, 1024, 1024, 512, 1024 },
            new[] { 1024, 512, 512, 512, 256, 512 }
        };
        public static readonly string[] LevelNames = { "Off (no shrinking)", "Gentle", "Balanced", "Strong", "Maximum" };
        public static readonly string[] LevelHelp =
        {
            "Only the lossless steps (unused links, duplicates, compression fixes, blend shapes).",
            "Shrinks only the very big textures. Almost no visible change.",
            "Colour textures to 2048, others to 1024. Face and eyes untouched. Good default.",
            "Colour 1024, others 512, Crunch compression for non-face textures. Visible on close inspection.",
            "Everything small (colour 512). Use only if you must reach a tight limit."
        };

        public static int CapForLevel(string category, int level, bool sensitive)
        {
            level = Mathf.Clamp(level, 0, 4);
            int idx = Array.IndexOf(AstTex.Categories, category);
            if (idx < 0) idx = AstTex.Categories.Length - 1;
            return sensitive ? SensCaps[level][idx] : LevelCaps[level][idx];
        }

        // sets target / compression / crunch on every texture row according to the plan
        public static void ApplyToRows(ScanData S, PlanData P)
        {
            foreach (var r in S.textures)
            {
                r.target = 0; r.fixCompression = false; r.planCrunch = false; r.apply = false;
                if (r.inPackages) continue;
                int longest = Mathf.Max(r.w, r.h);
                if (P.useFlat && r.flat)
                {
                    r.target = longest > 32 ? 32 : 0;
                    r.apply = r.target > 0;
                    continue;
                }
                int cap = CapForLevel(r.category, P.texLevel, r.sensitive);
                if (cap > 0 && longest > cap) r.target = cap;
                if (P.useCompress && ((AstSettings.FixCompression && r.uncompressed) || (AstSettings.HqToNormal && r.hq && !r.sensitive))) r.fixCompression = true;
                if (P.texLevel >= 3 && !r.sensitive && !r.isNormal && !r.crunch && longest >= 512) r.planCrunch = true;
                r.apply = r.target > 0 || r.fixCompression || r.planCrunch;
            }
        }

        public static List<Part> FindProposals(ScanData S)
        {
            var list = new List<KeyValuePair<Part, long>>();
            foreach (var p in S.parts)
            {
                if (p.go == null || p.renderer == null) continue;
                var info = AstRisk.Evaluate(S, p.go);
                if (info.level > Level.Info) continue;
                var sav = AstRisk.Estimate(S, new[] { p.go });
                long total = sav.tex + sav.mesh;
                if (total < 100 * 1024) continue;
                list.Add(new KeyValuePair<Part, long>(p, total));
            }
            return list.OrderByDescending(x => x.Key.go.activeInHierarchy ? 0 : 1).ThenByDescending(x => x.Value).Take(15).Select(x => x.Key).ToList();
        }

        public static void Build(ScanData S, PlanData P, Action<string, float> progress)
        {
            if (progress != null) progress("Looking for flat-colour textures...", 0.1f);
            AstTex.DetectFlat(S.textures, null);
            if (progress != null) progress("Looking for duplicate textures...", 0.3f);
            P.dups = AstDup.Find(S, null);
            if (progress != null) progress("Analysing blend shapes...", 0.55f);
            P.blend = AstBlend.Analyze(S, null);
            P.audio = AstAudio.Find(S);
            P.proposals = FindProposals(S);
            P.approved.Clear();
            P.built = true;
            ApplyToRows(S, P);
        }

        // estimated memory / download size, now (plan=false) or after the ticked plan (plan=true)
        public static Est Compute(ScanData S, PlanData P, bool plan)
        {
            double mem = 0, dl = 0;
            var removedTex = new HashSet<TexRow>();
            var delR = new HashSet<Renderer>();
            if (plan)
            {
                if (P.useDup) foreach (var g in P.dups) if (!g.blocked) foreach (var r in g.remove) removedTex.Add(r);
                foreach (var go in P.approved)
                    if (go != null) foreach (var r in go.GetComponentsInChildren<Renderer>(true)) delR.Add(r);
            }
            foreach (var t in S.textures)
            {
                if (removedTex.Contains(t)) continue;
                if (delR.Count > 0 && t.users.Count > 0 && t.users.All(u => u == null || delR.Contains(u))) continue;
                long m = plan ? AstTex.AfterIfApplied(t) : t.vram;
                bool cr = plan ? (t.crunch || (t.apply && t.planCrunch)) : t.crunch;
                mem += m;
                dl += m * AstSize.Ratio(cr);
            }
            double stale = (plan && P.useStale) ? 0 : S.staleBytes;
            mem += stale;
            dl += stale * AstSize.TexRatio;
            foreach (var mi in S.meshes)
            {
                if (delR.Count > 0 && mi.users.Count > 0 && mi.users.All(u => u == null || delR.Contains(u))) continue;
                double b = mi.bytes;
                if (plan && P.useBlend)
                {
                    var bs = P.blend.FirstOrDefault(x => x.mesh == mi.mesh && x.enabled && x.skipWhy == null);
                    if (bs != null) b = Math.Max(0.0, b - bs.RemoveBytes);
                }
                mem += b;
                dl += b * AstSize.MeshRatio;
            }
            double au = S.audioBytes;
            if (plan && P.useAudio) au = Math.Max(0.0, au - P.audio.Sum(a => a.saveEst));
            mem += au;
            dl += au * AstSize.AudioRatio;
            mem += S.animBytes;
            dl += S.animBytes * AstSize.AnimRatio;
            dl *= AstSize.Factor(S.avatar);
            var e = new Est();
            e.mem = mem;
            e.dl = dl;
            return e;
        }

        public static double Pick(Est e, int basis) { return basis == 0 ? e.dl : e.mem; }

        // chooses the gentlest settings that reach the target; never removes accessories on its own
        public static FitResult AutoFit(ScanData S, PlanData P, double targetBytes, int basis)
        {
            P.useStale = P.useDup = P.useFlat = P.useCompress = P.useBlend = P.useAudio = true;
            var fr = new FitResult();
            for (int lvl = 0; lvl <= 4; lvl++)
            {
                P.texLevel = lvl;
                ApplyToRows(S, P);
                double v = Pick(Compute(S, P, true), basis);
                fr.level = lvl;
                fr.estValue = v;
                if (v <= targetBytes) { fr.reached = true; break; }
            }
            if (!fr.reached)
            {
                var keep = new HashSet<GameObject>(P.approved);
                foreach (var pr in P.proposals) P.approved.Add(pr.go);
                fr.withProposals = Pick(Compute(S, P, true), basis);
                fr.needsProposals = true;
                P.approved = keep;
            }
            return fr;
        }
    }
}

namespace AvatarSlimmer3
{
    // "Put everything back the way it was": reverts every texture / material / audio change made after a backup was taken,
    // then restores the scene file from that backup. A safety copy of the CURRENT state is saved first, so even a restore can be undone.
    internal static class AstRestore
    {
        public static string SceneKey(GameObject avatar)
        {
            Scene sc = avatar.scene;
            return AstUtil.SafeName(string.IsNullOrEmpty(sc.name) ? "Untitled" : sc.name);
        }

        public static string Stamp(string assetPath)
        {
            var m = Regex.Match(Path.GetFileNameWithoutExtension(assetPath), @"(\d{8}_\d{6})");
            return m.Success ? m.Groups[1].Value : "";
        }

        // the pinned "original state" backup of this scene, or else the oldest backup that exists
        public static string Baseline(GameObject avatar)
        {
            string key = SceneKey(avatar);
            string b = AstBackup.GetBaseline(key);
            if (!string.IsNullOrEmpty(b) && File.Exists(AstTex.ProjectPathOf(b))) return b;
            var mine = AstBackup.List().Where(p => Path.GetFileName(p).StartsWith(key + "_", StringComparison.Ordinal)).ToList();
            return mine.Count > 0 ? mine[mine.Count - 1] : null;     // List() is newest first, so the last one is the oldest
        }

        public static int CountChangesAfter(string backupPath)
        {
            string stamp = Stamp(backupPath);
            int n = 0;
            foreach (var log in AstTex.ListLogs())
            {
                string fn = Path.GetFileName(log);
                string ls = fn.Length >= 15 ? fn.Substring(0, 15) : "";
                if (string.CompareOrdinal(ls, stamp) > 0) n++;
            }
            return n;
        }

        public static string RestoreEverything(GameObject avatar, string backupPath)
        {
            var sb = new StringBuilder();
            string stamp = Stamp(backupPath);
            AstBackup.Create(avatar, "before restore");
            int reverted = 0;
            foreach (var log in AstTex.ListLogs())       // newest first
            {
                string fn = Path.GetFileName(log);
                string ls = fn.Length >= 15 ? fn.Substring(0, 15) : "";
                if (string.CompareOrdinal(ls, stamp) > 0) reverted += AstTex.Revert(log);
            }
            sb.Append("Reverted " + reverted + " texture / material / audio change(s). ");
            Scene scene = avatar.scene;
            string orig = scene.path;
            if (string.IsNullOrEmpty(orig))
            {
                EditorSceneManager.OpenScene(backupPath, OpenSceneMode.Single);
                sb.Append("The scene was never saved, so the backup was opened instead (use File > Save As).");
            }
            else
            {
                File.Copy(AstTex.ProjectPathOf(backupPath), AstTex.ProjectPathOf(orig), true);
                AssetDatabase.ImportAsset(orig, ImportAssetOptions.ForceUpdate);
                EditorSceneManager.OpenScene(orig, OpenSceneMode.Single);
                sb.Append("Scene '" + orig + "' restored from " + Path.GetFileName(backupPath) + ".");
            }
            AstLog.Add("RESTORE: " + sb);
            return sb.ToString();
        }
    }
}

namespace AvatarSlimmer3
{
    // =====================================================================================
    //  The window: lifecycle, header, tabs, shared helpers.  (tabs live in the other partial parts)
    // =====================================================================================
    public partial class AvatarSlimmerV3Window : EditorWindow
    {
        const string Version = "3.0";
        const int TOverview = 0, TParts = 1, TOptimize = 2, TTextures = 3, TOutfits = 4, THistory = 5;
        static readonly string[] TabNames = { "Overview", "Parts", "Smart Plan", "Textures", "Outfits", "History" };

        [SerializeField] GameObject avatar;
        [SerializeField] int tab;

        ScanData S;
        bool stale;
        string status = "";
        Level statusLevel = Level.Info;

        // ---------------------------------------------------------------- styles
        GUIStyle stTitle, stH2, stWrap, stMini, stBadge, stCardName, stRight;

        void EnsureStyles()
        {
            if (stTitle != null && stH2 != null) return;
            stTitle = new GUIStyle(EditorStyles.boldLabel);
            stTitle.fontSize = 15;
            stH2 = new GUIStyle(EditorStyles.boldLabel);
            stH2.fontSize = 12;
            stWrap = new GUIStyle(EditorStyles.label);
            stWrap.wordWrap = true;
            stMini = new GUIStyle(EditorStyles.miniLabel);
            stMini.wordWrap = true;
            stBadge = new GUIStyle(EditorStyles.miniBoldLabel);
            stBadge.alignment = TextAnchor.MiddleCenter;
            stCardName = new GUIStyle(EditorStyles.boldLabel);
            stCardName.clipping = TextClipping.Clip;
            stRight = new GUIStyle(EditorStyles.label);
            stRight.alignment = TextAnchor.MiddleRight;
        }

        // ---------------------------------------------------------------- menu / lifecycle
        [MenuItem("Tools/Avatar Slimmer V3")]
        static void Open()
        {
            var w = GetWindow<AvatarSlimmerV3Window>("Avatar Slimmer V3");
            w.minSize = new Vector2(760, 600);
        }

        void OnEnable()
        {
            EditorApplication.update += OnEditorUpdate;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.delayCall += () => { if (this != null) AutoStart(); };
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            Undo.undoRedoPerformed -= OnUndoRedo;
            DestroyPreviewStuff();
        }

        void OnUndoRedo()
        {
            if (S != null) { stale = true; S.riskCache.Clear(); }
            Repaint();
        }

        // ---------------------------------------------------------------- helpers
        void SetStatus(string msg, Level lvl)
        {
            status = msg;
            statusLevel = lvl;
        }

        // run something after the current GUI pass (safe for dialogs, scans and scene changes)
        void Defer(Action a)
        {
            EditorApplication.delayCall += () =>
            {
                if (this == null) return;
                try { a(); }
                catch (ExitGUIException) { }
                catch (Exception e)
                {
                    Debug.LogException(e);
                    SetStatus("Error: " + e.Message, Level.Danger);
                }
                finally { EditorUtility.ClearProgressBar(); }
                Repaint();
            };
        }

        static void Progress(string msg, float f)
        {
            EditorUtility.DisplayProgressBar("Avatar Slimmer", msg, f);
        }

        void ClearDerived(bool keepThumbs = false)
        {
            selectedGo = null;
            ticked.Clear();
            tickVer++;
            blendPlan = null;
            outfitInfo = null;
            treeOpen.Clear();
            plan = new PlanData();
            fit = null;
            planVer++;
            planNumVer = -1;
            texSel = null;
            ClearCompare();
            if (!keepThumbs) ClearThumbs();
        }

        void DoScan(bool keepThumbs = false)
        {
            if (avatar == null) return;
            ClearDerived(keepThumbs);
            S = null;
            S = AstScan.Run(avatar, Progress);
            stale = false;
            int bad = S.issues.Count(i => i.level == Level.Danger);
            int warn = S.issues.Count(i => i.level == Level.Warn);
            SetStatus("Scan finished: " + S.parts.Count + " parts, " + S.textures.Count + " textures, " + bad + " danger / " + warn + " warning findings.", bad > 0 ? Level.Danger : warn > 0 ? Level.Warn : Level.Ok);
        }

        string lastDelta = "";

        void Rescan()
        {
            long[] before = S != null ? new[] { (long)S.totalTris, S.texBytes, S.meshBytes, (long)S.slotCount, S.audioBytes } : null;
            // keep the user's tick marks across a rescan where the objects still exist
            var keep = new HashSet<GameObject>(ticked.Where(g => g != null));
            var sel = selectedGo;
            var oldTab = tab;
            DoScan(true);
            if (before != null && S != null)
            {
                string d = "";
                if (before[0] != S.totalTris) d += "triangles " + before[0].ToString("N0") + " -> " + S.totalTris.ToString("N0") + ";   ";
                if (before[1] != S.texBytes) d += "textures " + AstUtil.MB(before[1]) + " -> " + AstUtil.MB(S.texBytes) + ";   ";
                if (before[2] != S.meshBytes) d += "meshes " + AstUtil.MB(before[2]) + " -> " + AstUtil.MB(S.meshBytes) + ";   ";
                if (before[3] != S.slotCount) d += "material slots " + before[3] + " -> " + S.slotCount + ";   ";
                if (d.Length > 0)
                {
                    lastDelta = DateTime.Now.ToString("HH:mm") + "   " + d;
                    AstLog.Add("Change: " + d);
                }
            }
            foreach (var g in keep) if (g != null) ticked.Add(g);
            selectedGo = sel;
            tab = oldTab;
            tickVer++;
        }

        // ---------------------------------------------------------------- main GUI
        void OnGUI()
        {
            EnsureStyles();
            DrawHeader();
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Press 'Find my avatar' and I will look for it in the scene by myself. (Or drag the avatar ROOT, the object that has the VRC Avatar Descriptor, into the field above.)", MessageType.Info);
                if (GUILayout.Button("Find my avatar automatically", GUILayout.Height(30))) Defer(() => AutoFind(true));
                foreach (var d in detected)
                {
                    if (d == null) continue;
                    var pick = d;
                    if (GUILayout.Button("Use:  " + d.name))
                        Defer(() => { avatar = pick; S = null; ClearDerived(); DoScan(); tab = TOptimize; });
                }
                return;
            }
            if (EditorUtility.IsPersistent(avatar))
            {
                EditorGUILayout.HelpBox("This is a file in the Project window. Drag the avatar from the Hierarchy (the scene) instead, so changes can be undone.", MessageType.Warning);
                return;
            }
            if (S == null)
            {
                EditorGUILayout.HelpBox("Press 'Scan avatar'.\nTip: commit your project to git first. The tool also saves a scene backup before every destructive step.", MessageType.Info);
                return;
            }
            if (stale)
                EditorGUILayout.HelpBox("The scene changed (undo/redo or edits). Press 'Rescan' so the numbers match.", MessageType.Warning);

            // IMGUI rule: structure-changing state is read into a local BEFORE the control that edits it,
            // so the layout pass and the event pass always see the same controls.
            int tabNow = tab;
            tab = GUILayout.Toolbar(tab, TabNames, GUILayout.Height(26));
            GUILayout.Space(2);
            try
            {
                switch (tabNow)
                {
                    case TOverview: DrawOverview(); break;
                    case TParts: DrawParts(); break;
                    case TOptimize: DrawOptimize(); break;
                    case TTextures: DrawTextures(); break;
                    case TOutfits: DrawOutfits(); break;
                    default: DrawHistory(); break;
                }
            }
            catch (ExitGUIException) { throw; }
            catch (Exception e)
            {
                if (Event.current.type == EventType.Repaint)
                    Debug.LogException(e);
                GUILayout.Space(6);
                EditorGUILayout.HelpBox("Something went wrong while drawing this tab:\n" + e.Message + "\n\nPress Rescan. If it keeps happening, copy the error and send it for a fix.", MessageType.Error);
                if (GUILayout.Button("Copy error to clipboard")) EditorGUIUtility.systemCopyBuffer = e.ToString();
            }
            DrawStatusBar();
        }

        void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                var oc = GUI.contentColor;
                GUI.contentColor = new Color(0.5f, 0.95f, 0.6f);
                GUILayout.Label("NEW V3   -   Smart Plan (target size)  |  Size estimate  |  Restore everything  |  Parts gallery", EditorStyles.miniBoldLabel);
                GUI.contentColor = oc;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Avatar Slimmer V3", stTitle);
                GUILayout.FlexibleSpace();
                GUILayout.Label("v" + Version, EditorStyles.miniLabel);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                var picked = (GameObject)EditorGUILayout.ObjectField("Avatar (root object)", avatar, typeof(GameObject), true);
                if (GUILayout.Button("Use selected", GUILayout.Width(95)) && Selection.activeGameObject != null)
                    picked = Selection.activeGameObject;
                if (picked != avatar)
                {
                    avatar = picked;
                    S = null;
                    ClearDerived();
                }
                using (new EditorGUI.DisabledScope(avatar == null || EditorUtility.IsPersistent(avatar)))
                {
                    if (GUILayout.Button(S == null ? "Scan avatar" : "Rescan", GUILayout.Width(95)))
                    {
                        var bk = tab;
                        bool first = S == null;
                        Defer(() => { Rescan(); tab = first ? TOptimize : bk; });
                    }
                }
            }
        }

        void DrawStatusBar()
        {
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                var old = GUI.contentColor;
                GUI.contentColor = AstUtil.LevelColor(statusLevel == Level.Info ? Level.Info : statusLevel);
                GUILayout.Label(string.IsNullOrEmpty(status) ? "Ready." : status, EditorStyles.wordWrappedMiniLabel);
                GUI.contentColor = old;
            }
        }

        // small coloured badge
        void Badge(Rect r, string text, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.Box(r, GUIContent.none);
            GUI.color = old;
            var oc = GUI.contentColor;
            GUI.contentColor = Color.black;
            GUI.Label(r, text, stBadge);
            GUI.contentColor = oc;
        }
    }
}

namespace AvatarSlimmer3
{
    public partial class AvatarSlimmerV3Window
    {
        // ---------------------------------------------------------------- state used by Overview / Parts
        Vector2 scrollOverview, scrollParts, scrollTree, scrollSide;
        GameObject selectedGo;
        readonly HashSet<GameObject> ticked = new HashSet<GameObject>();
        int tickVer, savVer = -1;
        Savings savCache;
        int partsView;      // 0 = gallery, 1 = tree
        int partFilter;
        string search = "";
        readonly HashSet<GameObject> treeOpen = new HashSet<GameObject>();
        static readonly string[] FilterNames = { "All parts", "Looks safe to delete", "Caution or danger", "Danger only", "Hidden objects" };

        // previews
        PreviewRenderUtility pru;
        Material fallbackMat;
        float yaw = 180f, pitch = 10f;
        bool showFull;
        Editor fullPreview;
        readonly Dictionary<GameObject, Texture2D> thumbs = new Dictionary<GameObject, Texture2D>();
        readonly Queue<Part> thumbQueue = new Queue<Part>();
        readonly HashSet<GameObject> thumbQueued = new HashSet<GameObject>();
        readonly HashSet<GameObject> thumbFailed = new HashSet<GameObject>();

        // ================================================================ OVERVIEW
        void DrawOverview()
        {
            using (var sv = new EditorGUILayout.ScrollViewScope(scrollOverview))
            {
            scrollOverview = sv.scrollPosition;
            if (lastDelta.Length > 0) EditorGUILayout.HelpBox("Last change:   " + lastDelta, MessageType.None);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("Estimated download:  " + AstSize.FormatMB(AstSize.Download(S)) + "      in memory:  " + AstSize.FormatMB(AstSize.Memory(S)), stH2);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Open Smart Plan", GUILayout.Width(130))) tab = TOptimize;
            }
            DrawBiggest();
            GUILayout.Label("Numbers vs VRChat PC performance ranks", stH2);
            foreach (var row in S.stats)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(row[0], GUILayout.Width(250));
                    GUILayout.Label(row[1], GUILayout.Width(220));
                    var old = GUI.contentColor;
                    GUI.contentColor = AstUtil.RankColor(row[2]);
                    GUILayout.Label(row[2], EditorStyles.boldLabel);
                    GUI.contentColor = old;
                }
            }
            GUILayout.Space(4);
            EditorGUILayout.HelpBox("Hidden and disabled objects still count for the rank and the file size. Real download size is only known after Build & Test in the VRChat SDK (PC limit 200 MB, Android limit 10 MB).", MessageType.None);
            GUILayout.Space(6);

            int d = S.issues.Count(i => i.level == Level.Danger);
            int w = S.issues.Count(i => i.level == Level.Warn);
            int n = S.issues.Count(i => i.level == Level.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Findings: " + d + " danger, " + w + " warning, " + n + " info", stH2);
                GUILayout.FlexibleSpace();
                var fixables = S.issues.Where(i => i.fix != null).ToList();
                using (new EditorGUI.DisabledScope(fixables.Count == 0))
                {
                    if (GUILayout.Button("Fix all fixable (" + fixables.Count + ")", GUILayout.Width(170)))
                        Defer(() =>
                        {
                            if (AstSettings.AutoBackup) AstBackup.Create(avatar, "before quick fixes");
                            foreach (var i in fixables) i.fix();
                            Rescan();
                            SetStatus("Applied " + fixables.Count + " quick fix(es).", Level.Ok);
                        });
                }
            }
            if (d + w == 0) EditorGUILayout.HelpBox("No blocking problems found by these checks. Still do a Build & Test before uploading.", MessageType.Info);

            foreach (var i in S.issues)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    string text = i.title + (string.IsNullOrEmpty(i.detail) ? "" : "\n" + i.detail);
                    var type = i.level == Level.Danger ? MessageType.Error : i.level == Level.Warn ? MessageType.Warning : MessageType.Info;
                    EditorGUILayout.HelpBox(text, type);
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(120)))
                    {
                        if (i.target != null && GUILayout.Button("Select"))
                        {
                            Selection.activeGameObject = i.target;
                            EditorGUIUtility.PingObject(i.target);
                        }
                        if (i.fix != null && GUILayout.Button(i.fixLabel ?? "Fix"))
                        {
                            var issue = i;
                            Defer(() =>
                            {
                                if (AstSettings.AutoBackup) AstBackup.Create(avatar, "before quick fix");
                                issue.fix();
                                Rescan();
                            });
                        }
                        if (i.gotoTab >= 0 && GUILayout.Button("Open tab")) tab = i.gotoTab;
                    }
                }
            }
            }
        }

        void DrawBiggest()
        {
            GUILayout.Label("The biggest things in this avatar", stH2);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                foreach (var t in S.textures.Take(5))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label("Texture  " + t.tex.name, GUILayout.Width(300));
                        GUILayout.Label(t.w + "x" + t.h + "   " + AstUtil.MB(t.vram), GUILayout.Width(180));
                        GUILayout.Label("used by " + AstUtil.Names(t.userNames, 2), stMini);
                    }
                }
                foreach (var m in S.meshes.OrderByDescending(x => x.bytes).Take(3))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label("Mesh  " + m.mesh.name, GUILayout.Width(300));
                        GUILayout.Label(AstUtil.MB(m.bytes) + (m.blendCount > 0 ? "   " + m.blendCount + " blend shapes" : ""), GUILayout.Width(180));
                    }
                }
                if (S.staleBytes > 0)
                    GUILayout.Label("Hidden: " + AstUtil.MB(S.staleBytes) + " of unused texture links (Smart Plan clears them).", stMini);
            }
            GUILayout.Label("What I recommend", stH2);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                int shown = 0;
                if (S.staleBytes >= 512 * 1024) { GUILayout.Label("1. Clear " + AstUtil.MB(S.staleBytes) + " of unused texture links (lossless). Smart Plan > Build plan.", stWrap); shown++; }
                int big = S.textures.Count(t => Mathf.Max(t.w, t.h) >= 4096);
                if (big > 0) { GUILayout.Label((shown + 1) + ". " + big + " texture(s) are 4096 px or more. Shrinking the ones that are not face/eyes saves the most.", stWrap); shown++; }
                int unc = S.textures.Count(t => t.uncompressed);
                if (unc > 0) { GUILayout.Label((shown + 1) + ". " + unc + " texture(s) are uncompressed (about 4x bigger than needed).", stWrap); shown++; }
                int shapes = S.meshes.Sum(m => m.blendCount);
                if (shapes > 100) { GUILayout.Label((shown + 1) + ". " + shapes + " blend shapes: the unused ones can be removed safely.", stWrap); shown++; }
                if (AstSize.Download(S) > 10L * 1048576 && shown == 0) { GUILayout.Label("The avatar is within the PC limits. For Android (10 MB) use Smart Plan with a 10 MB target.", stWrap); shown++; }
                if (shown == 0) GUILayout.Label("Nothing big stands out. The avatar is already lean.", stWrap);
                GUILayout.Label("Then open Smart Plan, set the size you want, and press Build plan.", stMini);
            }
        }

        // ================================================================ PARTS
        List<Part> FilteredParts()
        {
            IEnumerable<Part> q = S.parts.Where(p => p.go != null);
            if (!string.IsNullOrEmpty(search))
                q = q.Where(p => p.path.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || p.kind.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            switch (partFilter)
            {
                case 1: q = q.Where(p => AstRisk.Evaluate(S, p.go).level <= Level.Info); break;
                case 2: q = q.Where(p => AstRisk.Evaluate(S, p.go).level >= Level.Warn); break;
                case 3: q = q.Where(p => AstRisk.Evaluate(S, p.go).level == Level.Danger); break;
                case 4: q = q.Where(p => !p.go.activeInHierarchy); break;
            }
            return q.ToList();
        }

        void SetTick(GameObject g, bool on)
        {
            if (g == null) return;
            bool had = ticked.Contains(g);
            if (on && !had) { ticked.Add(g); tickVer++; }
            else if (!on && had) { ticked.Remove(g); tickVer++; }
        }

        void RefreshSavings()
        {
            if (savVer == tickVer) return;
            savCache = AstRisk.Estimate(S, ticked.Where(g => g != null));
            savVer = tickVer;
        }

        void DrawParts()
        {
            // state that decides WHICH controls exist is read first (IMGUI layout rule)
            int viewNow = partsView;
            var selNow = selectedGo;
            var listNow = FilteredParts();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                partsView = GUILayout.Toolbar(partsView, new[] { "Gallery", "Tree (everything)" }, EditorStyles.toolbarButton, GUILayout.Width(210));
                GUILayout.Space(8);
                partFilter = EditorGUILayout.Popup(partFilter, FilterNames, EditorStyles.toolbarPopup, GUILayout.Width(150));
                search = EditorGUILayout.TextField(search, EditorStyles.toolbarTextField);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Tick all shown"))
                    foreach (var p in FilteredParts()) SetTick(p.go, true);
                if (GUILayout.Button("Tick none")) { ticked.Clear(); tickVer++; }
                if (GUILayout.Button("Invert"))
                    foreach (var p in FilteredParts()) SetTick(p.go, !ticked.Contains(p.go));
                if (GUILayout.Button("Tick safe (no warnings)"))
                    foreach (var p in S.parts) SetTick(p.go, p.renderer != null && AstRisk.Evaluate(S, p.go).level == Level.Ok);
                if (GUILayout.Button("Tick hidden + safe"))
                    foreach (var p in S.parts) if (p.go != null && !p.go.activeInHierarchy && p.renderer != null && AstRisk.Evaluate(S, p.go).level == Level.Ok) SetTick(p.go, true);
            }

            RefreshSavings();
            GUILayout.Label(ticked.Count + " ticked.  Deleting them frees about " + AstUtil.MB(savCache.tex) + " of textures + " + AstUtil.MB(savCache.mesh) + " of meshes  ("
                            + savCache.tris.ToString("N0") + " triangles, " + savCache.slots + " material slots)", EditorStyles.miniBoldLabel);

            float rightW = 330f;
            float leftW = Mathf.Max(280f, position.width - rightW - 26f);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(leftW)))
                {
                    if (viewNow == 0) DrawGallery(leftW, listNow); else DrawTree(leftW);
                }
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(rightW)))
                {
                    using (var sv = new EditorGUILayout.ScrollViewScope(scrollSide, GUILayout.Height(ListHeight())))
                    {
                        scrollSide = sv.scrollPosition;
                        DrawSidePanel(rightW - 18f, selNow);
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(ticked.Count == 0))
                {
                    if (GUILayout.Button("Hide ticked (test)", GUILayout.Height(26)))
                    {
                        var l = ticked.ToList();
                        Defer(() => { AstDelete.Hide(l); S.riskCache.Clear(); tickVer++; SetStatus("Hid " + l.Count + " object(s). Ctrl+Z brings them back.", Level.Ok); });
                    }
                    if (GUILayout.Button("Show ticked", GUILayout.Height(26)))
                    {
                        var l = ticked.ToList();
                        Defer(() => { AstDelete.Show(l); S.riskCache.Clear(); tickVer++; });
                    }
                    if (GUILayout.Button("Select ticked in Scene", GUILayout.Height(26)))
                        Selection.objects = ticked.Where(g => g != null).Cast<UnityEngine.Object>().ToArray();
                    var oldBg = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(1f, 0.55f, 0.5f);
                    if (GUILayout.Button("Delete ticked", GUILayout.Height(26)))
                    {
                        var l = ticked.ToList();
                        Defer(() => ConfirmAndDelete(l));
                    }
                    GUI.backgroundColor = oldBg;
                }
            }
        }

        float ListHeight() { return Mathf.Max(200f, position.height - 335f); }

        // ---------------------------------------------------------------- gallery
        void DrawGallery(float width, List<Part> list)
        {
            float cardW = 128f, cardH = 176f;
            int cols = Mathf.Max(1, (int)((width - 24f) / cardW));
            int rows = Mathf.CeilToInt(list.Count / (float)cols);
            float viewH = ListHeight();
            using (var sv = new EditorGUILayout.ScrollViewScope(scrollParts, GUILayout.Height(viewH)))
            {
                scrollParts = sv.scrollPosition;
                Rect content = GUILayoutUtility.GetRect(cols * cardW, Mathf.Max(rows * cardH, 10f));
                for (int i = 0; i < list.Count; i++)
                {
                    int cx = i % cols, cy = i / cols;
                    var r = new Rect(content.x + cx * cardW, content.y + cy * cardH, cardW - 6f, cardH - 6f);
                    if (r.yMax < scrollParts.y - 10f || r.y > scrollParts.y + viewH + 10f) continue;
                    DrawCard(r, list[i]);
                }
            }
            GUILayout.Label(list.Count == 0 ? "Nothing matches the filter." : " ", EditorStyles.centeredGreyMiniLabel);
        }

        void DrawPlaceholder(Rect r, Part p)
        {
            Texture icon = null;
            if (p.kind.Contains("Light")) icon = AstUtil.Icon("Light Icon");
            else if (p.kind.Contains("Audio")) icon = AstUtil.Icon("AudioSource Icon");
            else if (p.kind.Contains("Particles")) icon = AstUtil.Icon("ParticleSystem Icon");
            if (icon != null) GUI.DrawTexture(new Rect(r.center.x - 16f, r.center.y - 16f, 32f, 32f), icon, ScaleMode.ScaleToFit);
            else if (p.mesh != null && !thumbFailed.Contains(p.go)) GUI.Label(r, "...", EditorStyles.centeredGreyMiniLabel);
            else GUI.Label(r, "no preview", EditorStyles.centeredGreyMiniLabel);
        }

        void DrawCard(Rect r, Part p)
        {
            var info = AstRisk.Evaluate(S, p.go);
            bool sel = p.go == selectedGo;
            GUI.Box(r, GUIContent.none);
            if (sel) EditorGUI.DrawRect(r, new Color(0.3f, 0.55f, 1f, 0.22f));
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 4f), AstUtil.LevelColor(info.level));
            var tr = new Rect(r.x + 6f, r.y + 8f, r.width - 12f, r.width - 12f);
            Texture2D th;
            if (thumbs.TryGetValue(p.go, out th) && th != null) GUI.DrawTexture(tr, th, ScaleMode.ScaleToFit);
            else { DrawPlaceholder(tr, p); RequestThumb(p); }
            GUI.Label(new Rect(r.x + 4f, tr.yMax + 2f, r.width - 8f, 16f), new GUIContent(p.go.name, p.path), stCardName);
            string line = p.kind + (p.tris > 0 ? "  " + p.tris.ToString("N0") + " tris" : "");
            GUI.Label(new Rect(r.x + 4f, tr.yMax + 16f, r.width - 8f, 14f), line, EditorStyles.miniLabel);
            Badge(new Rect(r.x + 6f, r.yMax - 22f, r.width - 12f, 16f), AstUtil.LevelText(info.level) + (p.go.activeInHierarchy ? "" : " (hidden)"), AstUtil.LevelColor(info.level));

            var tb = new Rect(r.x + 8f, r.y + 10f, 18f, 18f);
            bool was = ticked.Contains(p.go);
            bool now = GUI.Toggle(tb, was, GUIContent.none);
            if (now != was) SetTick(p.go, now);

            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition) && !tb.Contains(e.mousePosition))
            {
                selectedGo = p.go;
                e.Use();
                Repaint();
            }
        }

        // ---------------------------------------------------------------- tree
        void DrawTree(float width)
        {
            using (var sv = new EditorGUILayout.ScrollViewScope(scrollTree, GUILayout.Height(ListHeight())))
            {
                scrollTree = sv.scrollPosition;
                DrawTreeNode(S.avatar.transform, 0);
            }
        }

        void DrawTreeNode(Transform t, int depth)
        {
            if (t == null) return;
            var go = t.gameObject;
            bool isRoot = t == S.avatar.transform;
            bool hasKids = t.childCount > 0;
            bool open = isRoot || treeOpen.Contains(go);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(depth * 14f);
                if (hasKids && !isRoot)
                {
                    if (GUILayout.Button(open ? "v" : ">", EditorStyles.miniButton, GUILayout.Width(18f)))
                    {
                        if (open) treeOpen.Remove(go); else treeOpen.Add(go);
                    }
                }
                else GUILayout.Space(22f);

                if (!isRoot) SetTick(go, EditorGUILayout.Toggle(ticked.Contains(go), GUILayout.Width(16f)));
                else GUILayout.Space(20f);

                var old = GUI.contentColor;
                if (!go.activeSelf) GUI.contentColor = Color.gray;
                var style = go == selectedGo ? EditorStyles.boldLabel : EditorStyles.label;
                string label = go.name + (hasKids ? "  (" + t.childCount + ")" : "");
                if (GUILayout.Button(new GUIContent(label, AstUtil.PathTo(t, S.avatar.transform)), style, GUILayout.ExpandWidth(true)))
                    selectedGo = go;
                GUI.contentColor = old;

                if (!isRoot)
                {
                    var info = AstRisk.Evaluate(S, go);
                    var oc = GUI.contentColor;
                    GUI.contentColor = AstUtil.LevelColor(info.level);
                    GUILayout.Label(AstUtil.LevelText(info.level), EditorStyles.miniBoldLabel, GUILayout.Width(90f));
                    GUI.contentColor = oc;
                }
            }
            if (open && hasKids)
                foreach (Transform c in t) DrawTreeNode(c, depth + 1);
        }

        // ---------------------------------------------------------------- side panel (preview + details)
        void DrawSidePanel(float w, GameObject sel)
        {
            bool fullNow = showFull;
            showFull = EditorGUILayout.ToggleLeft("Show the whole avatar too (slow on big avatars)", showFull);
            if (!showFull && fullPreview != null) { DestroyImmediate(fullPreview); fullPreview = null; }

            Rect area = GUILayoutUtility.GetRect(w - 10f, 210f);
            if (fullNow)
            {
                var left = new Rect(area.x, area.y, area.width * 0.5f - 2f, area.height);
                var right = new Rect(area.x + area.width * 0.5f + 2f, area.y, area.width * 0.5f - 2f, area.height);
                DrawFullPreview(left);
                DrawPartPreview(right, sel);
            }
            else DrawPartPreview(area, sel);

            if (sel == null)
            {
                EditorGUILayout.HelpBox("Click a card (or a row in the Tree) to see its shape, what depends on it, and whether deleting it is safe.", MessageType.Info);
                return;
            }
            var info = AstRisk.Evaluate(S, sel);
            GUILayout.Label(AstUtil.PathTo(sel.transform, S.avatar.transform), stH2);
            var mt = info.level == Level.Danger ? MessageType.Error : info.level == Level.Warn ? MessageType.Warning : MessageType.Info;
            EditorGUILayout.HelpBox(info.verdict, mt);
            var sav = AstRisk.Estimate(S, new[] { sel });
            GUILayout.Label("Deleting it frees about " + AstUtil.MB(sav.tex) + " textures + " + AstUtil.MB(sav.mesh) + " meshes (" + sav.tris.ToString("N0") + " triangles).", stMini);
            foreach (var r in info.reasons)
            {
                var oc = GUI.contentColor;
                GUI.contentColor = AstUtil.LevelColor(r.level);
                GUILayout.Label("- " + r.text, stMini);
                GUI.contentColor = oc;
            }
            if (S.hasVRCFury || S.hasModularAvatar)
                GUILayout.Label("Note: VRCFury / Modular Avatar may hold links I cannot see. Test after changing.", stMini);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select in Hierarchy"))
                {
                    Selection.activeGameObject = sel;
                    EditorGUIUtility.PingObject(sel);
                }
                if (GUILayout.Button("Frame in Scene"))
                {
                    Selection.activeGameObject = sel;
                    var sv = SceneView.lastActiveSceneView;
                    if (sv != null) sv.FrameSelected();
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                var g = sel;
                if (GUILayout.Button(g.activeSelf ? "Hide" : "Show"))
                {
                    Undo.RecordObject(g, "Hide/Show");
                    g.SetActive(!g.activeSelf);
                    S.riskCache.Clear();
                }
                if (GUILayout.Button("Delete this one"))
                {
                    var l = new List<GameObject> { g };
                    Defer(() => ConfirmAndDelete(l));
                }
            }
        }

        void ConfirmAndDelete(List<GameObject> gos)
        {
            gos = AstDelete.TopMost(gos.Where(g => g != null && g != S.avatar)).ToList();
            if (gos.Count == 0)
            {
                EditorUtility.DisplayDialog("Nothing to delete", "Tick the objects you want to delete first.", "OK");
                return;
            }
            var infos = gos.Select(g => AstRisk.Evaluate(S, g)).ToList();
            int danger = infos.Count(i => i.level == Level.Danger);
            int warn = infos.Count(i => i.level == Level.Warn);
            int safe = infos.Count - danger - warn;
            var sav = AstRisk.Estimate(S, gos);
            string msg = "Delete " + gos.Count + " object(s)?\n\nSafe / probably ok: " + safe + "\nCaution: " + warn + "\nDanger: " + danger +
                         "\n\nThis frees about " + AstUtil.MB(sav.tex + sav.mesh) + " (" + sav.tris.ToString("N0") + " triangles).\n" +
                         (AstSettings.AutoBackup ? "A scene backup is saved first. " : "") + "You can undo with Ctrl+Z.";
            if (!EditorUtility.DisplayDialog("Delete objects", msg, "Delete", "Cancel")) return;
            if (danger > 0 && !EditorUtility.DisplayDialog("DANGER", danger + " of them are marked DANGER and would break the avatar. Delete anyway?", "Yes, delete anyway", "Cancel")) return;

            bool unpack = false;
            int locked = AstDelete.CountLocked(gos);
            if (locked > 0)
            {
                int choice = EditorUtility.DisplayDialogComplex("Part of a model/prefab",
                    locked + " object(s) belong to a model or prefab instance, and Unity does not allow deleting parts of those.\n\n" +
                    "Unpack the avatar (recommended, like right-click > Prefab > Unpack Completely) and delete, or just hide them instead?",
                    "Unpack and delete", "Cancel", "Hide instead");
                if (choice == 1) return;
                if (choice == 2)
                {
                    AstDelete.Hide(gos);
                    S.riskCache.Clear();
                    SetStatus("Hid " + gos.Count + " object(s) instead of deleting them.", Level.Ok);
                    return;
                }
                unpack = true;
            }
            int skipped;
            int n = AstDelete.Delete(S, gos, unpack, out skipped);
            Rescan();
            SetStatus("Deleted " + n + " object(s)" + (skipped > 0 ? ", " + skipped + " skipped" : "") + ". Freed about " + AstUtil.MB(sav.tex + sav.mesh) + ". Ctrl+Z undoes it.", n > 0 ? Level.Ok : Level.Warn);
        }

        // ================================================================ previews / thumbnails
        void EnsurePru()
        {
            if (pru != null) return;
            pru = new PreviewRenderUtility();
            pru.camera.fieldOfView = 30f;
            pru.camera.clearFlags = CameraClearFlags.SolidColor;
            pru.camera.backgroundColor = new Color(0.2f, 0.2f, 0.22f, 1f);
            pru.ambientColor = new Color(0.4f, 0.4f, 0.42f, 1f);
        }

        Material Fallback()
        {
            if (fallbackMat == null)
            {
                fallbackMat = new Material(Shader.Find("Standard"));
                fallbackMat.color = new Color(0.78f, 0.78f, 0.8f);
                fallbackMat.hideFlags = HideFlags.HideAndDontSave;
            }
            return fallbackMat;
        }

        void PlaceCamera(Bounds b, float yawDeg, float pitchDeg)
        {
            var cam = pru.camera;
            float radius = Mathf.Max(b.extents.magnitude, 0.0001f);
            var rot = Quaternion.Euler(pitchDeg, yawDeg, 0f);
            float dist = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.05f;
            cam.transform.rotation = rot;
            cam.transform.position = b.center - rot * Vector3.forward * dist;
            cam.nearClipPlane = Mathf.Max(dist * 0.02f, 0.0001f);
            cam.farClipPlane = dist * 3f + radius * 2f;
            pru.lights[0].transform.rotation = rot * Quaternion.Euler(25f, -30f, 0f);
            pru.lights[1].transform.rotation = rot * Quaternion.Euler(340f, 218f, 177f);
        }

        void QueueMesh(Part p)
        {
            var mats = p.renderer != null ? p.renderer.sharedMaterials : new Material[0];
            for (int s = 0; s < p.mesh.subMeshCount; s++)
            {
                Material m = s < mats.Length ? mats[s] : null;
                if (m == null || m.shader == null || AstSettings.PlainThumbs) m = Fallback();
                pru.DrawMesh(p.mesh, Matrix4x4.identity, m, s);
            }
        }

        Texture2D RenderThumb(Part p)
        {
            if (p.mesh == null) return null;
            EnsurePru();
            pru.BeginStaticPreview(new Rect(0, 0, 128, 128));
            try
            {
                PlaceCamera(p.mesh.bounds, 180f, 10f);
                QueueMesh(p);
                pru.Render();
            }
            finally { }
            return pru.EndStaticPreview();
        }

        void RequestThumb(Part p)
        {
            if (p.mesh == null || thumbQueued.Contains(p.go) || thumbFailed.Contains(p.go)) return;
            thumbQueued.Add(p.go);
            thumbQueue.Enqueue(p);
        }

        void OnEditorUpdate()
        {
            if (S == null || tab != TParts || partsView != 0 || thumbQueue.Count == 0) return;
            int budget = 2;
            while (budget-- > 0 && thumbQueue.Count > 0)
            {
                var p = thumbQueue.Dequeue();
                if (p == null || p.go == null || thumbs.ContainsKey(p.go)) continue;
                try
                {
                    var t = RenderThumb(p);
                    if (t != null) thumbs[p.go] = t; else thumbFailed.Add(p.go);
                }
                catch (Exception) { thumbFailed.Add(p.go); }
            }
            Repaint();
        }

        void ClearThumbs()
        {
            foreach (var kv in thumbs) if (kv.Value != null) DestroyImmediate(kv.Value);
            thumbs.Clear();
            thumbQueue.Clear();
            thumbQueued.Clear();
            thumbFailed.Clear();
        }

        void DestroyPreviewStuff()
        {
            ClearCompare();
            ClearThumbs();
            if (fullPreview != null) DestroyImmediate(fullPreview);
            fullPreview = null;
            if (pru != null) pru.Cleanup();
            pru = null;
            if (fallbackMat != null) DestroyImmediate(fallbackMat);
            fallbackMat = null;
        }

        void DrawFullPreview(Rect r)
        {
            if (avatar == null) return;
            if (fullPreview == null || fullPreview.target != avatar)
            {
                if (fullPreview != null) DestroyImmediate(fullPreview);
                fullPreview = Editor.CreateEditor(avatar);
            }
            if (fullPreview != null && fullPreview.HasPreviewGUI())
                fullPreview.OnInteractivePreviewGUI(r, EditorStyles.helpBox);
            else
                GUI.Label(r, "No preview available", EditorStyles.centeredGreyMiniLabel);
        }

        void DrawPartPreview(Rect r, GameObject go)
        {
            Part p = null;
            if (go != null) S.partByGo.TryGetValue(go, out p);
            if (p == null || p.go == null || p.mesh == null)
            {
                GUI.Box(r, GUIContent.none);
                GUI.Label(r, go == null ? "Click a card to preview its shape" : "This object has no mesh to preview", EditorStyles.centeredGreyMiniLabel);
                return;
            }
            var e = Event.current;
            if (e.type == EventType.MouseDrag && r.Contains(e.mousePosition))
            {
                yaw -= e.delta.x * 0.7f;
                pitch = Mathf.Clamp(pitch - e.delta.y * 0.7f, -85f, 85f);
                e.Use();
                Repaint();
            }
            if (e.type != EventType.Repaint) return;
            EnsurePru();
            Texture tex;
            pru.BeginPreview(r, GUIStyle.none);
            try
            {
                PlaceCamera(p.mesh.bounds, yaw, pitch);
                QueueMesh(p);
                pru.Render();
            }
            finally { tex = pru.EndPreview(); }
            if (tex != null) GUI.DrawTexture(r, tex, ScaleMode.StretchToFill, false);
        }
    }
}

namespace AvatarSlimmer3
{
    public partial class AvatarSlimmerV3Window
    {
        // ---------------------------------------------------------------- state
        List<BsMesh> blendPlan;
        Vector2 scrollOpt, scrollTex, scrollTexAll;
        bool texSettingsOpen;
        bool texOnlyChanges = false;
        bool texAssetPreviewLoading;
        static readonly string[] TargetLabels = { "keep", "32", "64", "128", "256", "512", "1024", "2048", "4096", "8192" };
        static readonly int[] TargetValues = { 0, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 };
        static readonly string[] CapLabels = { "256", "512", "1024", "2048", "4096", "8192" };
        static readonly int[] CapValues = { 256, 512, 1024, 2048, 4096, 8192 };

        // ---------------------------------------------------------------- blend shapes
        void DrawBlendSection()
        {
            GUILayout.Label("Unused blend shapes", stH2);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("Blend shapes (shape keys) that no animation, menu, lip sync, eye blink or component uses are removed from a COPY of the mesh (saved in Assets/AvatarSlimmerV3Window/Meshes). " +
                                "The original mesh is never changed, and the descriptor's eyelid indices are fixed automatically.", stWrap);
                AstSettings.ProtectVrcShapes = EditorGUILayout.ToggleLeft("Always keep VRChat names (vrc.*)", AstSettings.ProtectVrcShapes);
                AstSettings.ProtectMmdShapes = EditorGUILayout.ToggleLeft("Always keep MMD-style names (Japanese characters)", AstSettings.ProtectMmdShapes);
                AstSettings.KeepShapes = EditorGUILayout.TextField(new GUIContent("Also keep names containing", "Comma separated, for example: smile, blush"), AstSettings.KeepShapes);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Analyse blend shapes", GUILayout.Height(24)))
                        Defer(() => { blendPlan = AstBlend.Analyze(S, Progress); plan.blend = blendPlan; planVer++; SetStatus("Blend shape analysis done.", Level.Ok); });
                    using (new EditorGUI.DisabledScope(blendPlan == null || !blendPlan.Any(b => b.enabled && b.RemoveCount > 0)))
                    {
                        if (GUILayout.Button("Remove the unused ones", GUILayout.Height(24))) Defer(ApplyBlend);
                    }
                }

                if (blendPlan == null)
                {
                    GUILayout.Label("Not analysed yet.", EditorStyles.centeredGreyMiniLabel);
                    return;
                }
                if (blendPlan.Count == 0)
                {
                    GUILayout.Label("No mesh on this avatar has blend shapes.", EditorStyles.centeredGreyMiniLabel);
                    return;
                }
                long total = blendPlan.Where(b => b.skipWhy == null).Sum(b => b.TotalBytes);
                long rem = blendPlan.Where(b => b.enabled && b.skipWhy == null).Sum(b => b.RemoveBytes);
                GUILayout.Label("Blend shape data: about " + AstUtil.MB(total) + " in total, about " + AstUtil.MB(rem) + " can be removed.", EditorStyles.miniBoldLabel);

                foreach (var bs in blendPlan)
                {
                    bool wasExpanded = bs.expanded;   // read before the Foldout edits it (keeps IMGUI layout stable)
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(bs.skipWhy != null))
                        {
                            bool en = EditorGUILayout.Toggle(bs.enabled, GUILayout.Width(16));
                            if (en != bs.enabled) { bs.enabled = en; planVer++; }
                        }
                        bs.expanded = EditorGUILayout.Foldout(bs.expanded, bs.mesh.name + "   (" + bs.shapes.Count + " shapes, " + bs.RemoveCount + " removable, saves ~" + AstUtil.MB(bs.RemoveBytes) + ")", true);
                    }
                    if (bs.skipWhy != null) { GUILayout.Label("    " + bs.skipWhy, stMini); continue; }
                    if (!wasExpanded) continue;
                    foreach (var sh in bs.shapes.OrderBy(x => x.keep).ThenBy(x => x.index))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(24);
                            bool locked = sh.why.Length > 0 && sh.why != "kept by you";
                            using (new EditorGUI.DisabledScope(locked))
                            {
                                bool keep = EditorGUILayout.ToggleLeft(sh.name, sh.keep, GUILayout.Width(230));
                                if (keep != sh.keep) { sh.keep = keep; sh.why = keep ? "kept by you" : ""; planVer++; }
                            }
                            var old = GUI.contentColor;
                            GUI.contentColor = sh.keep ? new Color(0.6f, 0.8f, 1f) : new Color(1f, 0.8f, 0.3f);
                            GUILayout.Label(sh.keep ? "keep: " + sh.why : "REMOVE  (~" + AstUtil.MB(sh.bytes) + ")", EditorStyles.miniLabel);
                            GUI.contentColor = old;
                        }
                    }
                }
            }
        }

        void ApplyBlend()
        {
            var todo = blendPlan.Where(b => b.enabled && b.skipWhy == null && b.RemoveCount > 0).ToList();
            if (todo.Count == 0) return;
            string msg = "Remove " + todo.Sum(b => b.RemoveCount) + " unused blend shape(s) from " + todo.Count + " mesh(es)?\n\n" +
                         "New slim meshes are created and assigned to the avatar; the originals stay untouched.\n" +
                         (AstSettings.AutoBackup ? "A scene backup is saved first. " : "") + "Ctrl+Z undoes it.";
            if (S.hasVRCFury || S.hasModularAvatar) msg += "\n\nNote: VRCFury/Modular Avatar can use blend shapes that this scan cannot see by name only when they are stored as text. Test with Build & Test afterwards.";
            if (!EditorUtility.DisplayDialog("Remove unused blend shapes", msg, "Remove", "Cancel")) return;
            if (AstSettings.AutoBackup) AstBackup.Create(avatar, "before removing blend shapes");
            var res = AstBlend.Apply(S, blendPlan, Progress);
            foreach (var m in res.messages) AstLog.Add(m);
            blendPlan = null;
            Rescan();
            SetStatus("Removed " + res.shapes + " blend shape(s) from " + res.meshes + " mesh(es); saved about " + AstUtil.MB(res.saved) + ". Ctrl+Z undoes it.", Level.Ok);
        }

        // ---------------------------------------------------------------- d4rkAvatarOptimizer helper
        void DrawOptimizerHelper()
        {
            GUILayout.Label("d4rkAvatarOptimizer (merges meshes & materials at upload)", stH2);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("This tool shrinks FILE SIZE (textures, blend shapes, unused objects). d4rkAvatarOptimizer improves FPS by merging meshes and materials when you upload. " +
                                "Use both: run Auto Slim first, then let the optimizer work on the slimmer avatar.", stWrap);
                Type t = null;
                foreach (var tt in TypeCache.GetTypesDerivedFrom<MonoBehaviour>())
                    if (tt.Name == "d4rkAvatarOptimizer") { t = tt; break; }
                if (S.hasD4rkOptimizer)
                    EditorGUILayout.HelpBox("d4rkAvatarOptimizer is already on this avatar.", MessageType.Info);
                else if (t == null)
                    EditorGUILayout.HelpBox("d4rkAvatarOptimizer is not installed in this project (add it from VCC: d4rkpl4y3r.d4rkavataroptimizer).", MessageType.None);
                else if (GUILayout.Button("Add d4rkAvatarOptimizer to this avatar"))
                {
                    var type = t;
                    Defer(() =>
                    {
                        Undo.AddComponent(avatar, type);
                        Rescan();
                        SetStatus("Added d4rkAvatarOptimizer. Check its settings in the Inspector (the Standard preset is a safe start).", Level.Ok);
                    });
                }
            }
        }

        // ================================================================ TEXTURES
        void DrawTextures()
        {
            using (var outer = new EditorGUILayout.ScrollViewScope(scrollTexAll))
            {
                scrollTexAll = outer.scrollPosition;
                EditorGUILayout.HelpBox("Face / eye / head textures are NOT pre-ticked, so the important parts keep their sharpness. Flat single-colour textures are only listed, never changed unless you tick them. " +
                                        "Every change is logged so you can revert it in the History tab.", MessageType.Info);

                bool settingsWereOpen = texSettingsOpen;   // read before the foldout edits it (keeps IMGUI layout stable)
                texSettingsOpen = EditorGUILayout.Foldout(texSettingsOpen, "Settings  (limits per texture type, protection, compression)", true);
                if (settingsWereOpen)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        bool smart = EditorGUILayout.ToggleLeft("Smart limits per texture type", AstSettings.SmartCaps);
                        if (smart != AstSettings.SmartCaps) AstSettings.SmartCaps = smart;
                        if (AstSettings.SmartCaps)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                AstSettings.CapColor = EditorGUILayout.IntPopup("Colour", AstSettings.CapColor, CapLabels, CapValues);
                                AstSettings.CapNormal = EditorGUILayout.IntPopup("Normal", AstSettings.CapNormal, CapLabels, CapValues);
                            }
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                AstSettings.CapMask = EditorGUILayout.IntPopup("Mask / metal", AstSettings.CapMask, CapLabels, CapValues);
                                AstSettings.CapEmission = EditorGUILayout.IntPopup("Emission", AstSettings.CapEmission, CapLabels, CapValues);
                            }
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                AstSettings.CapMatCap = EditorGUILayout.IntPopup("MatCap", AstSettings.CapMatCap, CapLabels, CapValues);
                                AstSettings.CapOther = EditorGUILayout.IntPopup("Other", AstSettings.CapOther, CapLabels, CapValues);
                            }
                        }
                        else AstSettings.SingleCap = EditorGUILayout.IntPopup("Max size for every texture", AstSettings.SingleCap, CapLabels, CapValues);
                        AstSettings.ProtectFace = EditorGUILayout.ToggleLeft("Protect face / eye / head textures", AstSettings.ProtectFace);
                        AstSettings.FixCompression = EditorGUILayout.ToggleLeft("Compress textures that are stored uncompressed (about 4x smaller)", AstSettings.FixCompression);
                        AstSettings.HqToNormal = EditorGUILayout.ToggleLeft("Change 'High Quality' compression to 'Normal' (VRChat: High Quality makes the avatar bigger)", AstSettings.HqToNormal);
                        GUILayout.Label("After changing settings press 'Re-plan with these settings'.", stMini);
                    }
                }
                texOnlyChanges = EditorGUILayout.ToggleLeft("Only show textures that would change", texOnlyChanges);

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Re-plan with these settings"))
                    {
                        AstTex.ReplanAll(S.textures);
                        planVer++;
                    }
                    if (GUILayout.Button("Find flat-colour textures"))
                        Defer(() =>
                        {
                            int n = AstTex.DetectFlat(S.textures, Progress);
                            SetStatus(n + " texture(s) look like a single flat colour (they are flagged below; tick them to shrink to 32x32).", n > 0 ? Level.Warn : Level.Ok);
                        });
                    if (GUILayout.Button("Tick all flat"))
                    {
                        foreach (var r in S.textures) if (r.flat && !r.inPackages) { r.target = 32; r.apply = true; }
                        planVer++;
                    }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Tick all suggested (not face/eye)"))
                    {
                        foreach (var r in S.textures) r.apply = !r.inPackages && !r.flat && !r.sensitive && (r.target > 0 || r.fixCompression);
                        planVer++;
                    }
                    if (GUILayout.Button("Untick all"))
                    {
                        foreach (var r in S.textures) r.apply = false;
                        planVer++;
                    }
                }

                var rows = S.textures.Where(r => !texOnlyChanges || r.target > 0 || r.fixCompression || r.flat).ToList();
                long total = S.textures.Sum(r => r.vram);
                var sel = S.textures.Where(r => r.apply).ToList();
                long saved = sel.Sum(r => Math.Max(0L, r.vram - AstTex.After(r)));
                GUILayout.Label(S.textures.Count + " textures, about " + AstUtil.MB(total) + " in memory.   Ticked: " + sel.Count + "  (saves about " + AstUtil.MB(saved) + ")", EditorStyles.miniBoldLabel);

                // ---- virtualised table (fixed height, scrolls inside)
                float rowH = 46f;
                float viewH = settingsWereOpen ? 340f : Mathf.Clamp(position.height - 380f, 240f, 700f);
                texAssetPreviewLoading = false;
                using (var sv = new EditorGUILayout.ScrollViewScope(scrollTex, GUILayout.Height(viewH)))
                {
                    scrollTex = sv.scrollPosition;
                    float w = Mathf.Max(800f, position.width - 60f);
                    Rect content = GUILayoutUtility.GetRect(w, Mathf.Max(rows.Count * rowH, 10f));
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var r = new Rect(content.x, content.y + i * rowH, w, rowH - 2f);
                        if (r.yMax < scrollTex.y - 10f || r.y > scrollTex.y + viewH + 10f) continue;
                        DrawTexRow(r, rows[i]);
                    }
                }
                if (texAssetPreviewLoading) Repaint();
                GUILayout.Label(rows.Count == 0 ? "No textures to show with this filter." : " ", EditorStyles.centeredGreyMiniLabel);

                DrawCompare();
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(sel.Count == 0))
                    {
                        var oldBg = GUI.backgroundColor;
                        GUI.backgroundColor = new Color(0.55f, 0.9f, 0.6f);
                        if (GUILayout.Button("Apply to ticked textures", GUILayout.Height(30))) Defer(ApplyTextures);
                        GUI.backgroundColor = oldBg;
                    }
                    int changedN = S.textures.Count(t => t.changedByTool);
                    using (new EditorGUI.DisabledScope(changedN == 0))
                    {
                        if (GUILayout.Button("Restore all " + changedN + " reduced texture(s)", GUILayout.Height(30)))
                            Defer(() =>
                            {
                                if (!EditorUtility.DisplayDialog("Restore textures", "Put " + changedN + " texture(s) back to their ORIGINAL import settings (size and compression)?", "Restore", "Cancel")) return;
                                foreach (var t in S.textures.Where(x => x.changedByTool).ToList()) AstTex.RestoreOne(t);
                                AstLog.Add("Restored " + changedN + " texture(s) to their original import settings.");
                                Rescan();
                                SetStatus("Restored " + changedN + " texture(s).", Level.Ok);
                            });
                    }
                }
            }
        }

        void DrawTexRow(Rect r, TexRow row)
        {
            GUI.Box(r, GUIContent.none);
            float x = r.x + 4f;
            using (new EditorGUI.DisabledScope(row.inPackages))
            {
                bool on = GUI.Toggle(new Rect(x, r.y + 14f, 18f, 18f), row.apply, GUIContent.none);
                if (on != row.apply) { row.apply = on; planVer++; }
            }
            x += 24f;
            Texture th = AssetPreview.GetAssetPreview(row.tex);
            if (th == null)
            {
                th = AssetPreview.GetMiniThumbnail(row.tex);
                if (AssetPreview.IsLoadingAssetPreview(row.tex.GetInstanceID())) texAssetPreviewLoading = true;
            }
            if (th != null) GUI.DrawTexture(new Rect(x, r.y + 4f, 38f, 38f), th, ScaleMode.ScaleToFit);
            x += 44f;
            if (GUI.Button(new Rect(x, r.y + 4f, 210f, 18f), new GUIContent(row.tex.name, row.path), EditorStyles.linkLabel))
            {
                EditorGUIUtility.PingObject(row.tex);
                texSel = row;
            }
            string flags = (row.changedByTool ? "REDUCED by tool (was " + row.orig.oldMax + ")  " : "") + (row.flat ? "FLAT COLOUR?  " : "") + (row.sensitive ? "face/eye  " : "") + (row.uncompressed ? "uncompressed  " : "") + (row.hq ? "HQ  " : "") + (row.crunch ? "crunch  " : "") + (row.inPackages ? "Packages (read-only)  " : "");
            var oc = GUI.contentColor;
            if (row.flat || row.changedByTool) GUI.contentColor = new Color(1f, 0.6f, 0.3f);
            GUI.Label(new Rect(x, r.y + 22f, 210f, 18f), flags + "used by " + AstUtil.Names(row.userNames, 2), EditorStyles.miniLabel);
            GUI.contentColor = oc;
            x += 216f;
            GUI.Label(new Rect(x, r.y + 14f, 78f, 18f), row.w + "x" + row.h);
            x += 80f;
            GUI.Label(new Rect(x, r.y + 14f, 64f, 18f), AstUtil.MB(row.vram));
            x += 66f;
            GUI.Label(new Rect(x, r.y + 14f, 70f, 18f), row.category);
            x += 72f;
            if (!row.inPackages)
            {
                int nt = EditorGUI.IntPopup(new Rect(x, r.y + 13f, 70f, 18f), row.target, TargetLabels, TargetValues);
                if (nt != row.target) { row.target = nt; row.apply = nt > 0 || row.fixCompression; planVer++; }
            }
            x += 76f;
            long after = AstTex.After(row);
            var oc2 = GUI.contentColor;
            GUI.contentColor = after < row.vram ? new Color(0.5f, 0.9f, 0.5f) : Color.white;
            GUI.Label(new Rect(x, r.y + 14f, 100f, 18f), (row.apply && after < row.vram) ? "-> " + AstUtil.MB(after) : "no change");
            GUI.contentColor = oc2;
            x += 104f;
            if (row.changedByTool && GUI.Button(new Rect(x, r.y + 12f, 74f, 20f), "Restore"))
            {
                var rr = row;
                Defer(() =>
                {
                    AstTex.RestoreOne(rr);
                    AstLog.Add("Restored texture '" + rr.tex.name + "' to its original import settings.");
                    Rescan();
                    SetStatus("Restored '" + rr.tex.name + "'.", Level.Ok);
                });
            }
        }

        // ---------------------------------------------------------------- quality compare (before / after)
        TexRow texSel;
        RenderTexture cmpRT;
        TexRow cmpFor;
        int cmpSize, cmpZoom = 2;
        static readonly string[] ZoomLabels = { "Whole image", "Zoom 2x", "Zoom 4x", "Zoom 8x" };
        static readonly float[] ZoomFrac = { 1f, 0.5f, 0.25f, 0.125f };

        void ClearCompare()
        {
            if (cmpRT != null) { cmpRT.Release(); DestroyImmediate(cmpRT); cmpRT = null; }
            cmpFor = null;
        }

        Texture ReducedTexture(TexRow r, int size)
        {
            if (cmpRT != null && (cmpFor != r || cmpSize != size)) ClearCompare();
            if (cmpRT == null)
            {
                cmpRT = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32);
                cmpRT.filterMode = FilterMode.Bilinear;
                Graphics.Blit(r.tex, cmpRT);
                cmpFor = r;
                cmpSize = size;
            }
            return cmpRT;
        }

        void DrawCompare()
        {
            GUILayout.Label("Quality check (click a texture name in the table)", stH2);
            var r = texSel;
            int zoomNow = cmpZoom;
            cmpZoom = EditorGUILayout.Popup("Zoom", cmpZoom, ZoomLabels);
            Rect area = GUILayoutUtility.GetRect(10, 228, GUILayout.ExpandWidth(true));
            if (r == null || r.tex == null)
            {
                GUI.Label(area, "Pick a texture to see how it looks now (left) and after the chosen size (right).", EditorStyles.centeredGreyMiniLabel);
                return;
            }
            int cur = Mathf.Max(r.w, r.h);
            int after = r.target > 0 ? Mathf.Clamp(r.target, 32, 8192) : cur;
            var left = new Rect(area.x, area.y, 200f, 200f);
            var right = new Rect(area.x + 214f, area.y, 200f, 200f);
            float f = ZoomFrac[Mathf.Clamp(zoomNow, 0, 3)];
            var uv = new Rect(0.5f - f / 2f, 0.5f - f / 2f, f, f);
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTextureWithTexCoords(left, r.tex, uv);
                Texture red = after >= cur ? (Texture)r.tex : ReducedTexture(r, Mathf.Min(after, 4096));
                GUI.DrawTextureWithTexCoords(right, red, uv);
            }
            GUI.Label(new Rect(left.x, left.yMax + 2f, 200f, 18f), "Now:  " + cur + " px", EditorStyles.miniBoldLabel);
            GUI.Label(new Rect(right.x, right.yMax + 2f, 220f, 18f), "After:  " + after + " px" + (after >= cur ? "  (no change)" : ""), EditorStyles.miniBoldLabel);
            if (r.srcMax > 0) GUI.Label(new Rect(right.xMax + 14f, left.y, 260f, 60f), "Original file: " + r.srcMax + " px\nCategory: " + r.category + (r.sensitive ? "\nFace / eye texture (protected)" : ""), EditorStyles.miniLabel);
        }

        void ApplyTextures()
        {
            var sel = S.textures.Where(r => r.apply && !r.inPackages).ToList();
            if (sel.Count == 0) return;
            long saved = sel.Sum(r => Math.Max(0L, r.vram - AstTex.After(r)));
            string msg = "Change the import settings of " + sel.Count + " texture(s)?\n\nSaves about " + AstUtil.MB(saved) + " of memory.\n" +
                         "These are project-wide import settings (other avatars that use the same textures change too).\n" +
                         "This cannot be undone with Ctrl+Z, but a revert log is saved (History tab).";
            if (!EditorUtility.DisplayDialog("Apply texture changes", msg, "Apply", "Cancel")) return;
            string log = AstTex.Apply(S, sel);
            AstLog.Add("Textures: " + sel.Count + " ticked" + (log != null ? ", log " + log : ", nothing needed changing"));
            Rescan();
            SetStatus(log != null ? "Texture changes applied. Revert log: " + log : "Nothing needed changing.", log != null ? Level.Ok : Level.Info);
        }
    }
}

namespace AvatarSlimmer3
{
    public partial class AvatarSlimmerV3Window
    {
        // ---------------------------------------------------------------- state
        OutfitInfo outfitInfo;
        GameObject outfitSource;
        bool outfitReset = true, outfitHumanoid = true;
        Vector2 scrollOutfit, scrollHist;
        List<string> histBackups, histTexLogs;
        double histTime;
        string diagText = "";
        readonly Dictionary<string, string> histLogLabels = new Dictionary<string, string>();
        string histBaseline;
        int histChanges;

        // ================================================================ OUTFITS
        void DrawOutfits()
        {
            using (var sv = new EditorGUILayout.ScrollViewScope(scrollOutfit))
            {
                scrollOutfit = sv.scrollPosition;
                EditorGUILayout.HelpBox("Add clothes or accessories that were made for your avatar's body. Drag a prefab from the Project window (or an object from the scene) into the field. " +
                                        "The tool re-attaches the outfit's bones to your avatar's bones (humanoid role first, then bone name), keeps extra bones such as skirts or hair, " +
                                        "and removes the outfit's own armature. It is one Ctrl+Z, and a scene backup is saved first.", MessageType.Info);

                var srcNow = outfitSource;   // read before the field edits it (keeps IMGUI layout stable)
                var picked = (GameObject)EditorGUILayout.ObjectField("Outfit (prefab or scene object)", outfitSource, typeof(GameObject), true);
                if (picked != outfitSource) { outfitSource = picked; outfitInfo = null; }
                outfitReset = EditorGUILayout.ToggleLeft("Move the outfit to the avatar's origin (recommended)", outfitReset);
                outfitHumanoid = EditorGUILayout.ToggleLeft("Match bones by humanoid role first (needs both to be Humanoid rigs)", outfitHumanoid);

                bool invalid = srcNow == null || srcNow == avatar || (srcNow.scene.IsValid() && srcNow.transform.IsChildOf(avatar.transform));
                if (srcNow != null && invalid)
                    EditorGUILayout.HelpBox("This object is the avatar itself or already part of it.", MessageType.Warning);

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(invalid))
                    {
                        if (GUILayout.Button("Check bone matching", GUILayout.Height(26)))
                        {
                            var src = outfitSource;
                            var hum = outfitHumanoid;
                            Defer(() => { outfitInfo = AstOutfit.Analyze(avatar, src, hum); SetStatus("Bone matching checked.", Level.Info); });
                        }
                        using (new EditorGUI.DisabledScope(outfitInfo == null || !outfitInfo.CanApply))
                        {
                            var old = GUI.backgroundColor;
                            GUI.backgroundColor = new Color(0.55f, 0.9f, 0.6f);
                            if (GUILayout.Button("Add to avatar", GUILayout.Height(26))) Defer(AddOutfit);
                            GUI.backgroundColor = old;
                        }
                    }
                }

                if (outfitInfo != null)
                {
                    GUILayout.Space(4);
                    GUILayout.Label("Result of the check", stH2);
                    GUILayout.Label(outfitInfo.renderers + " skinned mesh(es), " + outfitInfo.bonesNeeded + " bones needed: " + outfitInfo.mapped.Count + " matched, " + outfitInfo.unmapped.Count + " without a match"
                                    + (outfitInfo.usedHumanoid ? "  (humanoid matching used)" : ""), stWrap);
                    foreach (var w in outfitInfo.warnings) EditorGUILayout.HelpBox(w, MessageType.Warning);
                    if (outfitInfo.CanApply && outfitInfo.warnings.Count == 0)
                        EditorGUILayout.HelpBox("Looks good. Press 'Add to avatar'.", MessageType.Info);
                    if (outfitInfo.mapped.Count > 0)
                        GUILayout.Label("Matched: " + AstUtil.Names(outfitInfo.mapped, 12), stMini);
                    if (outfitInfo.unmapped.Count > 0)
                        GUILayout.Label("No match (extra bones; used ones are kept): " + AstUtil.Names(outfitInfo.unmapped, 12), stMini);
                }
                EditorGUILayout.HelpBox("Turning an outfit on/off in the game menu needs a toggle in your menu (VRCFury or Modular Avatar). This tool only attaches it. " +
                                        "Extra clothes add size, so they go against the slimming goal; remove outfits you do not wear from the Parts tab.", MessageType.None);
            }
        }

        void AddOutfit()
        {
            var src = outfitSource;
            if (src == null || src == avatar) return;
            var info = AstOutfit.Analyze(avatar, src, outfitHumanoid);
            if (!info.CanApply)
            {
                EditorUtility.DisplayDialog("Cannot add this outfit", info.warnings.Count > 0 ? info.warnings[0] : "No bones matched.", "OK");
                outfitInfo = info;
                return;
            }
            string msg = "Add '" + src.name + "' to '" + avatar.name + "'?\n\n" + info.mapped.Count + " bones match, " + info.unmapped.Count + " have no match.\n" +
                         (AstSettings.AutoBackup ? "A scene backup is saved first. " : "") + "One Ctrl+Z undoes it.";
            if (!EditorUtility.DisplayDialog("Add outfit", msg, "Add", "Cancel")) return;
            string message;
            var inst = AstOutfit.Apply(S, src, outfitReset, outfitHumanoid, out message);
            if (inst == null)
            {
                SetStatus("Outfit not added. " + message, Level.Warn);
                return;
            }
            outfitInfo = null;
            Rescan();
            SetStatus("Outfit added. " + message, Level.Ok);
        }

        string BuildDiagnostics()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Avatar Slimmer V3  (code version " + Version + ")");
            sb.AppendLine("Unity: " + Application.unityVersion);
            try
            {
                var ms = MonoScript.FromScriptableObject(this);
                sb.AppendLine("This window runs from: " + (ms != null ? AssetDatabase.GetAssetPath(ms) : "?"));
            }
            catch (Exception) { sb.AppendLine("This window runs from: (unknown)"); }
            sb.AppendLine("Avatar: " + (avatar != null ? avatar.name : "-") + "   parts: " + (S != null ? S.parts.Count : 0) + "   textures: " + (S != null ? S.textures.Count : 0));
            sb.AppendLine("Scripts in the project whose name contains AvatarSafety / AvatarSlimmer / AvatarTexture:");
            var seen = new HashSet<string>();
            foreach (var key in new[] { "AvatarSafety", "AvatarSlimmer", "AvatarTexture" })
                foreach (var guid in AssetDatabase.FindAssets(key))
                {
                    string p = AssetDatabase.GUIDToAssetPath(guid);
                    if (seen.Add(p)) sb.AppendLine("   " + p);
                }
            if (seen.Count == 0) sb.AppendLine("   (none)");
            string root = Application.dataPath.Substring(0, Application.dataPath.Length - "Assets".Length);
            foreach (var f in new[] { "Packages/manifest.json", "Packages/vpm-manifest.json" })
            {
                try
                {
                    var hits = System.IO.File.ReadAllLines(root + f).Where(l => l.ToLowerInvariant().Contains("avatarsafety") || l.ToLowerInvariant().Contains("safety-tool") || l.ToLowerInvariant().Contains("avatarslimmer")).ToList();
                    sb.AppendLine(f + ": " + (hits.Count == 0 ? "no related lines" : ""));
                    foreach (var h in hits) sb.AppendLine("   " + h.Trim());
                }
                catch (Exception) { sb.AppendLine(f + ": not found"); }
            }
            return sb.ToString();
        }

        // ================================================================ HISTORY / SAFETY
        void RefreshHistory()
        {
            histBackups = AstBackup.List();
            histTexLogs = AstTex.ListLogs();
            histLogLabels.Clear();
            foreach (var lp in histTexLogs)
            {
                var lg = AstTex.ReadLog(lp);
                histLogLabels[lp] = lg != null
                    ? lg.created + "   " + lg.avatar + "   (" + lg.entries.Count + " textures, " + lg.matRefs.Count + " material links, " + lg.audio.Count + " audio)"
                    : System.IO.Path.GetFileName(lp);
            }
            histBaseline = avatar != null ? AstRestore.Baseline(avatar) : null;
            histChanges = histBaseline != null ? AstRestore.CountChangesAfter(histBaseline) : 0;
            histTime = EditorApplication.timeSinceStartup;
        }

        void DrawHistory()
        {
            if (histBackups == null || (Event.current.type == EventType.Layout && EditorApplication.timeSinceStartup - histTime > 4.0)) RefreshHistory();
            using (var sv = new EditorGUILayout.ScrollViewScope(scrollHist))
            {
                scrollHist = sv.scrollPosition;

                GUILayout.Label("Go back", stH2);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(histBaseline == null
                        ? "No backup yet. One is saved automatically before the first plan or delete."
                        : "Original-state backup:  " + System.IO.Path.GetFileName(histBaseline) + "      changes made since: " + histChanges + " batch(es)", stWrap);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(histBaseline == null))
                        {
                            var oldBg = GUI.backgroundColor;
                            GUI.backgroundColor = new Color(1f, 0.6f, 0.55f);
                            if (GUILayout.Button("Restore EVERYTHING to the original state", GUILayout.Height(28))) Defer(AskRestoreBaseline);
                            GUI.backgroundColor = oldBg;
                        }
                        if (GUILayout.Button("Use the current state as the new 'original'", GUILayout.Height(28)))
                            Defer(() =>
                            {
                                string np = AstBackup.Create(avatar, "new original state");
                                if (np != null) AstBackup.SetBaseline(AstRestore.SceneKey(avatar), np);
                                RefreshHistory();
                                SetStatus(np != null ? "The current state is now the original to restore to." : "Backup failed (see Console).", np != null ? Level.Ok : Level.Danger);
                            });
                    }
                }

                GUILayout.Space(6);
                GUILayout.Label("Safety settings", stH2);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    AstSettings.AutoBackup = EditorGUILayout.ToggleLeft("Save a scene backup before every destructive step (recommended)", AstSettings.AutoBackup);
                    AstSettings.BackupKeep = EditorGUILayout.IntSlider("Backups to keep", AstSettings.BackupKeep, 1, 50);
                    AstSettings.PlainThumbs = EditorGUILayout.ToggleLeft("Use plain grey shading for Parts thumbnails (if a shader renders wrongly)", AstSettings.PlainThumbs);
                    GUILayout.Label("Backups are copies of your scene saved in " + AstBackup.Folder + ". Open one to go back to how things were; your original mesh files are never modified.", stMini);
                }

                GUILayout.Space(6);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Scene backups", stH2);
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Create backup now", GUILayout.Width(150)))
                        Defer(() =>
                        {
                            string p = AstBackup.Create(avatar, "manual");
                            RefreshHistory();
                            SetStatus(p != null ? "Backup saved: " + p : "Backup failed (see Console).", p != null ? Level.Ok : Level.Danger);
                        });
                }
                if (histBackups.Count == 0) GUILayout.Label("No backups yet.", EditorStyles.centeredGreyMiniLabel);
                foreach (var p in histBackups)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(System.IO.Path.GetFileName(p));
                        if (GUILayout.Button("Show", GUILayout.Width(60)))
                            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(p));
                        if (GUILayout.Button("Open scene", GUILayout.Width(90)))
                        {
                            var path = p;
                            Defer(() => AstBackup.Open(path));
                        }
                        if (GUILayout.Button("Restore", GUILayout.Width(70)))
                        {
                            var path = p;
                            Defer(() => ConfirmRestore(path));
                        }
                    }
                }

                GUILayout.Space(6);
                GUILayout.Label("Changes made by plans (revert one batch)", stH2);
                if (histTexLogs.Count == 0) GUILayout.Label("No texture changes logged yet.", EditorStyles.centeredGreyMiniLabel);
                foreach (var p in histTexLogs)
                {
                    string label;
                    if (!histLogLabels.TryGetValue(p, out label)) label = System.IO.Path.GetFileName(p);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(label);
                        if (GUILayout.Button("Revert", GUILayout.Width(80)))
                        {
                            var path = p;
                            Defer(() =>
                            {
                                if (!EditorUtility.DisplayDialog("Revert texture changes", "Put these textures back to the settings they had before?", "Revert", "Cancel")) return;
                                int n = AstTex.Revert(path);
                                AstLog.Add("Reverted " + n + " texture(s) from " + path);
                                RefreshHistory();
                                if (S != null) Rescan();
                                SetStatus("Reverted " + n + " texture(s).", Level.Ok);
                            });
                        }
                    }
                }

                GUILayout.Space(6);
                GUILayout.Label("Diagnostics", stH2);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label("Shows which file this window runs from and lists any old copies of the tool in the project. Press the button and paste the result in the chat.", stMini);
                    if (GUILayout.Button("Copy diagnostic report to clipboard", GUILayout.Height(26)))
                    {
                        diagText = BuildDiagnostics();
                        EditorGUIUtility.systemCopyBuffer = diagText;
                        SetStatus("Diagnostic report copied to the clipboard.", Level.Ok);
                    }
                    if (diagText.Length > 0) EditorGUILayout.TextArea(diagText, GUILayout.MinHeight(90));
                }

                GUILayout.Space(6);
                GUILayout.Label("This session", stH2);
                if (AstLog.Lines.Count == 0) GUILayout.Label("Nothing yet.", EditorStyles.centeredGreyMiniLabel);
                for (int i = AstLog.Lines.Count - 1; i >= 0; i--)
                    GUILayout.Label(AstLog.Lines[i], stMini);
            }
        }
    }
}

namespace AvatarSlimmer3
{
    public partial class AvatarSlimmerV3Window
    {
        // ---------------------------------------------------------------- state
        PlanData plan = new PlanData();
        FitResult fit;
        int planVer, planNumVer = -1;
        readonly double[] stepSave = new double[7];     // 0 stale, 1 duplicates, 2 flat, 3 compression, 4 blend shapes, 5 audio, 6 texture level
        Est estNow, estAfter;
        string calibText = "";
        List<GameObject> detected = new List<GameObject>();

        // ================================================================ detect the avatar automatically
        List<GameObject> DetectAvatars()
        {
            var res = new List<GameObject>();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene sc = SceneManager.GetSceneAt(i);
                if (!sc.isLoaded) continue;
                foreach (var root in sc.GetRootGameObjects())
                    foreach (var c in root.GetComponentsInChildren<Component>(true))
                        if (c != null && c.GetType().Name == "VRCAvatarDescriptor" && !res.Contains(c.gameObject)) res.Add(c.gameObject);
            }
            return res;
        }

        // finds the avatar by itself; with several avatars in the scene the user picks one in the header
        void AutoFind(bool scanNow)
        {
            detected = DetectAvatars();
            if (detected.Count == 0)
            {
                SetStatus("No avatar found: no object with a VRC Avatar Descriptor is open in the scene.", Level.Warn);
                return;
            }
            if (detected.Count == 1)
            {
                avatar = detected[0];
                S = null;
                ClearDerived();
                if (scanNow) { DoScan(); tab = TOptimize; }
                SetStatus("Found your avatar: " + avatar.name + ".", Level.Ok);
            }
            else SetStatus("Found " + detected.Count + " avatars. Pick the one you want in the header.", Level.Info);
        }

        void AutoStart()
        {
            if (this == null || avatar != null || !AstSettings.SmartStart) return;
            Defer(() => AutoFind(true));
        }

        // ================================================================ small drawing helpers
        void DrawBar(double value, double max, Color fill, string label)
        {
            Rect r = GUILayoutUtility.GetRect(10, 18, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(r, new Color(0.16f, 0.16f, 0.17f));
            float f = max > 0 ? Mathf.Clamp01((float)(value / max)) : 0f;
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width * f, r.height), fill);
            var oc = GUI.contentColor;
            GUI.contentColor = Color.white;
            GUI.Label(r, "  " + label, EditorStyles.miniBoldLabel);
            GUI.contentColor = oc;
        }

        static Color LimitColor(double frac)
        {
            if (frac < 0.5) return new Color(0.25f, 0.65f, 0.3f);
            if (frac < 0.85) return new Color(0.8f, 0.65f, 0.15f);
            return new Color(0.8f, 0.3f, 0.25f);
        }

        void DrawBreakdown()
        {
            var parts = AstSize.Breakdown(S).Where(p => p.Value > 0).ToList();
            double total = parts.Sum(p => p.Value);
            if (total <= 0) return;
            Color[] cols = { new Color(0.35f, 0.6f, 0.95f), new Color(0.9f, 0.5f, 0.3f), new Color(0.5f, 0.8f, 0.45f), new Color(0.8f, 0.7f, 0.3f), new Color(0.7f, 0.5f, 0.85f) };
            Rect r = GUILayoutUtility.GetRect(10, 18, GUILayout.ExpandWidth(true));
            float x = r.x;
            var names = new List<string>();
            var all = AstSize.Breakdown(S);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Value <= 0) continue;
                float w = (float)(r.width * all[i].Value / total);
                EditorGUI.DrawRect(new Rect(x, r.y, w, r.height), cols[i % cols.Length]);
                x += w;
                names.Add(all[i].Key + " " + AstSize.FormatMB(all[i].Value) + " (" + (100.0 * all[i].Value / total).ToString("0") + "%)");
            }
            GUILayout.Label(string.Join("   |   ", names.ToArray()), stMini);
        }

        // ================================================================ SMART PLAN TAB
        void DrawOptimize()
        {
            using (var sv = new EditorGUILayout.ScrollViewScope(scrollOpt))
            {
                scrollOpt = sv.scrollPosition;
                DrawSizeCard();
                GUILayout.Space(6);
                DrawTargetBox();
                if (plan.built)
                {
                    RefreshPlanNumbers();
                    DrawPlanSteps();
                    DrawProposals();
                    DrawPlanResult();
                }
                else
                {
                    EditorGUILayout.HelpBox("Press 'Build plan' to let the tool study this avatar (flat textures, duplicate textures, unused texture links, unused blend shapes, audio). " +
                                            "Nothing is changed until you press 'Apply plan', and accessories are never removed unless you tick them.", MessageType.Info);
                }
                GUILayout.Space(8);
                DrawBlendSection();
                GUILayout.Space(8);
                DrawOptimizerHelper();
            }
        }

        void DrawSizeCard()
        {
            GUILayout.Label("How big is this avatar?", stH2);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                long dl = AstSize.Download(S);
                long mem = AstSize.Memory(S);
                bool cal = AstSize.IsCalibrated(S.avatar);
                GUILayout.Label("Estimated DOWNLOAD size:  " + AstSize.FormatMB(dl) + (cal ? "   (calibrated with your real number)" : "   (estimate)"), stH2);
                double pcFrac = dl / (200.0 * 1048576);
                DrawBar(dl, 200.0 * 1048576, LimitColor(pcFrac), "PC limit 200 MB  -  " + (pcFrac * 100).ToString("0.0") + "% used");
                double andFrac = dl / (10.0 * 1048576);
                DrawBar(dl, 10.0 * 1048576, LimitColor(andFrac), "Android / Quest limit 10 MB  -  " + (andFrac * 100).ToString("0") + "%" + (andFrac > 1 ? "   (too big for Android)" : ""));
                GUILayout.Space(2);
                GUILayout.Label("In memory (VRAM + RAM, approx):  " + AstSize.FormatMB(mem) + "     PC limit 500 MB  |  Android limit 40 MB", EditorStyles.boldLabel);
                DrawBreakdown();
                if (S.staleBytes > 0)
                    EditorGUILayout.HelpBox("Hidden bloat: " + AstSize.FormatMB(S.staleBytes) + " of textures are still linked by materials that no longer use them (" + S.staleRefs.Count + " link(s)). The plan below can remove them without changing how anything looks.", MessageType.Warning);

                GUILayout.Space(4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Real size from the VRChat SDK (MB):", GUILayout.Width(230));
                    calibText = EditorGUILayout.TextField(calibText, GUILayout.Width(70));
                    if (GUILayout.Button("Calibrate", GUILayout.Width(80)))
                    {
                        double v;
                        string t = (calibText ?? "").Replace(',', '.');
                        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && AstSize.Calibrate(S, v))
                        {
                            planVer++;
                            SetStatus("Calibrated: estimates for this avatar now follow your real number (" + v.ToString("0.0", CultureInfo.InvariantCulture) + " MB).", Level.Ok);
                        }
                        else SetStatus("Type the number the SDK shows, for example 11.8", Level.Warn);
                    }
                    if (cal && GUILayout.Button("Reset", GUILayout.Width(60))) { AstSize.ClearCalibration(S.avatar); planVer++; }
                }
                GUILayout.Label("The download number is an estimate. After a Build & Test, type the size the VRChat SDK shows and press Calibrate: later estimates for this avatar get much closer.", stMini);
            }
        }

        void DrawTargetBox()
        {
            GUILayout.Label("Target size", stH2);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label("Tell me how big you want it. I choose the gentlest steps that get there, and I show what each step costs in quality before anything is changed.", stWrap);
                int basisNow = AstSettings.TargetBasis;
                int nb = EditorGUILayout.Popup("Measure", basisNow, new[] { "Download size (estimate)", "Memory (VRAM + RAM)" });
                if (nb != basisNow) { AstSettings.TargetBasis = nb; planVer++; }
                AstSettings.TargetMB = EditorGUILayout.FloatField("Target (MB)", AstSettings.TargetMB);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Presets:", GUILayout.Width(60));
                    if (GUILayout.Button("Android 10")) AstSettings.TargetMB = 10f;
                    if (GUILayout.Button("20")) AstSettings.TargetMB = 20f;
                    if (GUILayout.Button("40")) AstSettings.TargetMB = 40f;
                    if (GUILayout.Button("60")) AstSettings.TargetMB = 60f;
                    if (GUILayout.Button("100")) AstSettings.TargetMB = 100f;
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(plan.built ? "Rebuild plan" : "Build plan", GUILayout.Height(28)))
                        Defer(() =>
                        {
                            AstPlan.Build(S, plan, Progress);
                            blendPlan = plan.blend;
                            fit = null;
                            planVer++;
                            SetStatus("Plan built. Choose steps below or press 'Auto-fit to target'.", Level.Ok);
                        });
                    using (new EditorGUI.DisabledScope(!plan.built))
                    {
                        if (GUILayout.Button("Auto-fit to target", GUILayout.Height(28)))
                            Defer(() =>
                            {
                                double tgt = AstSettings.TargetMB * 1048576.0;
                                fit = AstPlan.AutoFit(S, plan, tgt, AstSettings.TargetBasis);
                                planVer++;
                                SetStatus(fit.reached ? "Target reachable with the '" + AstPlan.LevelNames[fit.level] + "' texture level." : "Target not reachable without removing accessories (see the proposals below).", fit.reached ? Level.Ok : Level.Warn);
                            });
                    }
                }
            }
        }

        // ---------------------------------------------------------------- plan numbers (recomputed only when something changed)
        struct RowSnap { public int target; public bool fix, crunch, apply; }

        List<RowSnap> SnapRows()
        {
            var l = new List<RowSnap>(S.textures.Count);
            foreach (var r in S.textures) l.Add(new RowSnap { target = r.target, fix = r.fixCompression, crunch = r.planCrunch, apply = r.apply });
            return l;
        }

        void RestoreRows(List<RowSnap> l)
        {
            for (int i = 0; i < S.textures.Count && i < l.Count; i++)
            {
                var r = S.textures[i];
                r.target = l[i].target; r.fixCompression = l[i].fix; r.planCrunch = l[i].crunch; r.apply = l[i].apply;
            }
        }

        // a plan SETTING changed (step switch / texture level): re-derive the texture rows from it
        void PlanChanged()
        {
            AstPlan.ApplyToRows(S, plan);
            planVer++;
        }

        void RefreshPlanNumbers()
        {
            if (planNumVer == planVer) return;
            planNumVer = planVer;
            int basis = AstSettings.TargetBasis;
            var snap = SnapRows();     // what the rows say right now (the plan, or the user's manual edits in the Textures tab)
            stepSave[0] = SaveOf(basis, v => plan.useStale = v, () => plan.useStale);
            stepSave[1] = SaveOf(basis, v => plan.useDup = v, () => plan.useDup);
            stepSave[2] = SaveOf(basis, v => plan.useFlat = v, () => plan.useFlat);
            stepSave[3] = SaveOf(basis, v => plan.useCompress = v, () => plan.useCompress);
            stepSave[4] = SaveOf(basis, v => plan.useBlend = v, () => plan.useBlend);
            stepSave[5] = SaveOf(basis, v => plan.useAudio = v, () => plan.useAudio);
            int lvl = plan.texLevel;
            plan.texLevel = 0;
            AstPlan.ApplyToRows(S, plan);
            double off = AstPlan.Pick(AstPlan.Compute(S, plan, true), basis);
            plan.texLevel = lvl;
            AstPlan.ApplyToRows(S, plan);
            double on = AstPlan.Pick(AstPlan.Compute(S, plan, true), basis);
            stepSave[6] = off - on;
            RestoreRows(snap);
            estNow = AstPlan.Compute(S, plan, false);
            estAfter = AstPlan.Compute(S, plan, true);
        }

        double SaveOf(int basis, Action<bool> set, Func<bool> get)
        {
            bool was = get();
            set(false);
            AstPlan.ApplyToRows(S, plan);
            double off = AstPlan.Pick(AstPlan.Compute(S, plan, true), basis);
            set(true);
            AstPlan.ApplyToRows(S, plan);
            double on = AstPlan.Pick(AstPlan.Compute(S, plan, true), basis);
            set(was);
            return Math.Max(0.0, off - on);
        }

        bool StepRow(bool on, string title, string detail, double save, bool enabled)
        {
            bool res = on;
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!enabled))
                    res = EditorGUILayout.ToggleLeft(title, on, GUILayout.Width(300));
                GUILayout.Label(detail, stMini);
                GUILayout.FlexibleSpace();
                var oc = GUI.contentColor;
                GUI.contentColor = save > 0 ? new Color(0.5f, 0.9f, 0.5f) : Color.gray;
                GUILayout.Label(save > 0 ? "saves ~" + AstSize.FormatMB(save) : "nothing to do", EditorStyles.miniBoldLabel, GUILayout.Width(110));
                GUI.contentColor = oc;
            }
            return res;
        }

        void DrawPlanSteps()
        {
            GUILayout.Space(6);
            GUILayout.Label("Steps (the first ones change nothing you can see)", stH2);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                int dupCount = plan.dups.Count(g => !g.blocked);
                int blockedDup = plan.dups.Count(g => g.blocked);
                int flat = S.textures.Count(r => r.flat);
                int comp = S.textures.Count(r => !r.inPackages && ((AstSettings.FixCompression && r.uncompressed) || (AstSettings.HqToNormal && r.hq && !r.sensitive)));
                int shapes = plan.blend.Where(b => b.enabled && b.skipWhy == null).Sum(b => b.RemoveCount);
                int clips = plan.audio.Count(a => !a.already);

                bool v;
                v = StepRow(plan.useStale, "Clear unused texture links", S.staleRefs.Count + " link(s)  -  lossless", stepSave[0], S.staleRefs.Count > 0);
                if (v != plan.useStale) { plan.useStale = v; PlanChanged(); }
                v = StepRow(plan.useDup, "Merge duplicate textures", dupCount + " group(s)" + (blockedDup > 0 ? " (" + blockedDup + " blocked: in Packages)" : "") + "  -  lossless", stepSave[1], dupCount > 0);
                if (v != plan.useDup) { plan.useDup = v; PlanChanged(); }
                v = StepRow(plan.useBlend, "Remove unused blend shapes", shapes + " shape(s)  -  lossless", stepSave[4], shapes > 0);
                if (v != plan.useBlend) { plan.useBlend = v; PlanChanged(); }
                v = StepRow(plan.useFlat, "Shrink flat-colour textures to 32x32", flat + " texture(s)  -  verify the preview", stepSave[2], flat > 0);
                if (v != plan.useFlat) { plan.useFlat = v; PlanChanged(); }
                v = StepRow(plan.useCompress, "Fix texture compression", comp + " texture(s)  -  tiny quality change", stepSave[3], comp > 0);
                if (v != plan.useCompress) { plan.useCompress = v; PlanChanged(); }
                v = StepRow(plan.useAudio, "Compress audio clips (Vorbis)", clips + " clip(s)  -  small quality change", stepSave[5], clips > 0);
                if (v != plan.useAudio) { plan.useAudio = v; PlanChanged(); }

                GUILayout.Space(4);
                int lvlNow = plan.texLevel;
                int nl = EditorGUILayout.Popup("Texture shrinking", lvlNow, AstPlan.LevelNames);
                if (nl != lvlNow) { plan.texLevel = nl; PlanChanged(); }
                GUILayout.Label(AstPlan.LevelHelp[Mathf.Clamp(plan.texLevel, 0, 4)] + "   saves ~" + AstSize.FormatMB(stepSave[6]) + "   (face and eye textures are protected at the first three levels).", stMini);
                if (GUILayout.Button("Review / edit every texture (Textures tab)")) tab = TTextures;
            }
        }

        void DrawProposals()
        {
            GUILayout.Space(6);
            GUILayout.Label("Accessories I could remove (NOTHING is removed unless you tick it)", stH2);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                if (plan.proposals.Count == 0)
                {
                    GUILayout.Label("No accessory is both safe to remove and big enough to matter.", EditorStyles.centeredGreyMiniLabel);
                    return;
                }
                GUILayout.Label("These are safe to remove (nothing depends on them) and they use the most space. Tick the ones you agree to remove; you will see their names again before anything happens.", stWrap);
                foreach (var pr in plan.proposals)
                {
                    if (pr.go == null) continue;
                    var sav = AstRisk.Estimate(S, new[] { pr.go });
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        bool was = plan.approved.Contains(pr.go);
                        bool now = EditorGUILayout.ToggleLeft(pr.go.name + (pr.go.activeInHierarchy ? "" : "   (hidden)"), was, GUILayout.Width(300));
                        if (now != was) { if (now) plan.approved.Add(pr.go); else plan.approved.Remove(pr.go); planVer++; }
                        GUILayout.Label(pr.kind + "   " + (pr.tris > 0 ? pr.tris.ToString("N0") + " tris" : ""), stMini);
                        GUILayout.FlexibleSpace();
                        GUILayout.Label("frees ~" + AstSize.FormatMB(sav.tex + sav.mesh), EditorStyles.miniBoldLabel, GUILayout.Width(110));
                        if (GUILayout.Button("Select", GUILayout.Width(55)))
                        {
                            Selection.activeGameObject = pr.go;
                            EditorGUIUtility.PingObject(pr.go);
                        }
                    }
                }
            }
        }

        void DrawPlanResult()
        {
            GUILayout.Space(6);
            GUILayout.Label("Result", stH2);
            int basis = AstSettings.TargetBasis;
            double cur = AstPlan.Pick(estNow, basis);
            double aft = AstPlan.Pick(estAfter, basis);
            double tgt = AstSettings.TargetMB * 1048576.0;
            double max = Math.Max(cur, Math.Max(aft, tgt)) * 1.08;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawBar(cur, max, new Color(0.55f, 0.55f, 0.6f), "Now  " + AstSize.FormatMB(cur));
                DrawBar(aft, max, aft <= tgt ? new Color(0.25f, 0.65f, 0.3f) : new Color(0.8f, 0.55f, 0.2f), "After the plan  " + AstSize.FormatMB(aft));
                DrawBar(tgt, max, new Color(0.3f, 0.45f, 0.8f), "Your target  " + AstSize.FormatMB(tgt));
                bool reached = aft <= tgt;
                var mt = reached ? MessageType.Info : MessageType.Warning;
                string msg = reached
                    ? "The plan reaches your target (" + AstSize.FormatMB(aft) + ")."
                    : "The ticked steps give about " + AstSize.FormatMB(aft) + ", still above your target.";
                if (!reached && fit != null && fit.needsProposals)
                    msg += " Even the Maximum level is not enough, so the target needs removing accessories: with every proposal removed it would be about " + AstSize.FormatMB(fit.withProposals) + ".";
                EditorGUILayout.HelpBox(msg + "   (Estimates: confirm with Build & Test in the VRChat SDK.)", mt);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var old = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(0.55f, 0.9f, 0.6f);
                    if (GUILayout.Button("Apply plan", GUILayout.Height(32))) Defer(RunPlan);
                    GUI.backgroundColor = old;
                    GUI.backgroundColor = new Color(1f, 0.6f, 0.55f);
                    if (GUILayout.Button("Restore everything...", GUILayout.Height(32), GUILayout.Width(170))) Defer(AskRestoreBaseline);
                    GUI.backgroundColor = old;
                }
            }
        }

        // ---------------------------------------------------------------- apply the plan
        void RunPlan()
        {
            var approved = plan.approved.Where(g => g != null).ToList();
            var steps = new StringBuilder();
            if (plan.useStale && S.staleRefs.Count > 0) steps.AppendLine("- Clear " + S.staleRefs.Count + " unused texture link(s)");
            if (plan.useDup && plan.dups.Any(g => !g.blocked)) steps.AppendLine("- Merge " + plan.dups.Count(g => !g.blocked) + " group(s) of duplicate textures");
            int shapes = plan.blend.Where(b => b.enabled && b.skipWhy == null).Sum(b => b.RemoveCount);
            if (plan.useBlend && shapes > 0) steps.AppendLine("- Remove " + shapes + " unused blend shape(s)");
            int texN = S.textures.Count(r => r.apply);
            if (texN > 0) steps.AppendLine("- Change " + texN + " texture(s) (level: " + AstPlan.LevelNames[plan.texLevel] + ")");
            if (plan.useAudio && plan.audio.Any(a => !a.already)) steps.AppendLine("- Compress " + plan.audio.Count(a => !a.already) + " audio clip(s)");
            if (approved.Count > 0) steps.AppendLine("- REMOVE these accessories you approved:\n      " + string.Join("\n      ", approved.Select(g => g.name).ToArray()));
            if (steps.Length == 0)
            {
                EditorUtility.DisplayDialog("Nothing to do", "No step has anything to change.", "OK");
                return;
            }
            double aft = AstPlan.Pick(AstPlan.Compute(S, plan, true), AstSettings.TargetBasis);
            string msg = "The plan will:\n\n" + steps + "\nEstimated result: about " + AstSize.FormatMB(aft) + ".\n" +
                         "(Textures and materials are project-wide assets, so other avatars that share them change too, but they look the same.)\n" +
                         (AstSettings.AutoBackup ? "A scene backup is saved first, and 'Restore everything' can bring it all back." : "Auto-backup is OFF (History tab).");
            if (!EditorUtility.DisplayDialog("Apply plan", msg, "Apply", "Cancel")) return;

            if (AstSettings.AutoBackup) AstBackup.Create(avatar, "before plan");
            long beforeDl = AstSize.Download(S);
            var log = AstTex.NewLog(S);
            var rep = new StringBuilder();
            if (plan.useStale && S.staleRefs.Count > 0) rep.Append("Cleared " + AstMat.ClearStale(S.staleRefs, log) + " unused link(s). ");
            if (plan.useDup) { int n = AstDup.Apply(S, plan.dups, log); if (n > 0) rep.Append("Merged duplicates (" + n + " slot(s)). "); }
            if (plan.useBlend && shapes > 0)
            {
                var res = AstBlend.Apply(S, plan.blend, Progress);
                rep.Append("Blend shapes: removed " + res.shapes + ". ");
                foreach (var m in res.messages) AstLog.Add(m);
            }
            if (approved.Count > 0) DeleteApproved(approved, rep);
            if (plan.useAudio) { int n = AstAudio.Apply(plan.audio, log); if (n > 0) rep.Append("Audio: " + n + " clip(s). "); }
            var merged = new HashSet<TexRow>();
            if (plan.useDup) foreach (var g in plan.dups) if (!g.blocked) foreach (var r in g.remove) merged.Add(r);
            int tn = AstTex.ApplyRows(S.textures.Where(r => r.apply && !merged.Contains(r) && !(r.users.Count > 0 && r.users.All(u => u == null))).ToList(), log);
            if (tn > 0) rep.Append("Textures changed: " + tn + ". ");
            if (!log.IsEmpty) AstTex.WriteLog(log, "changes");
            AssetDatabase.SaveAssets();
            Rescan();
            long afterDl = AstSize.Download(S);
            string summary = "Plan applied. " + rep + "Estimated download: " + AstSize.FormatMB(beforeDl) + " -> " + AstSize.FormatMB(afterDl) + ".";
            SetStatus(summary, Level.Ok);
            AstLog.Add(summary);
        }

        void DeleteApproved(List<GameObject> approved, StringBuilder rep)
        {
            bool unpack = false;
            int locked = AstDelete.CountLocked(approved);
            if (locked > 0)
            {
                int choice = EditorUtility.DisplayDialogComplex("Part of a model/prefab",
                    locked + " approved accessory object(s) belong to a model or prefab instance, and Unity does not allow deleting parts of those.\n\nUnpack the avatar and delete them, or just hide them?",
                    "Unpack and delete", "Skip them", "Hide instead");
                if (choice == 1) { rep.Append("Accessory removal skipped. "); return; }
                if (choice == 2) { AstDelete.Hide(approved); S.riskCache.Clear(); rep.Append("Hid " + approved.Count + " accessory object(s). "); return; }
                unpack = true;
            }
            int skipped;
            int n = AstDelete.Delete(S, approved, unpack, out skipped);
            rep.Append("Removed " + n + " accessory object(s)" + (skipped > 0 ? " (" + skipped + " skipped)" : "") + ". ");
        }

        // ---------------------------------------------------------------- restore everything
        void AskRestoreBaseline()
        {
            string b = AstRestore.Baseline(avatar);
            if (b == null)
            {
                EditorUtility.DisplayDialog("No backup yet", "There is no scene backup to restore from. Backups are saved automatically before every plan or delete (History tab).", "OK");
                return;
            }
            ConfirmRestore(b);
        }

        void ConfirmRestore(string backupPath)
        {
            int n = AstRestore.CountChangesAfter(backupPath);
            string msg = "This puts EVERYTHING back the way it was when this backup was saved:\n" + System.IO.Path.GetFileName(backupPath) + "\n\n" +
                         "- the scene file is replaced by that backup (scene edits made after it are lost)\n" +
                         "- " + n + " batch(es) of texture / material / audio changes are reverted\n\n" +
                         "A safety copy of the CURRENT state is saved first, so even this can be undone.";
            if (!EditorUtility.DisplayDialog("Restore everything", msg, "Restore", "Cancel")) return;
            string res = AstRestore.RestoreEverything(avatar, backupPath);
            avatar = null;
            S = null;
            ClearDerived();
            AutoFind(true);
            SetStatus(res, Level.Ok);
        }
    }
}
#endif
