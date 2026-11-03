// Audit of Obfuz symbol obfuscation on the managed assemblies IL2CPP was fed
// (Build/*/<name>_BackUpThisFolder_ButDontShipItWithYourGame/Managed): those
// names are exactly what global-metadata.dat ships and what a dumper prints.
//
// usage: dotnet run --project Tools/ObfuscationAudit -- MANAGED_DIR [--list]
// Exit 0 when every type/member of the project assemblies is renamed except
// what Unity or Fusion must find by name; 1 when a sensitive symbol is left.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

internal static class Program
{
    private static readonly string[] Assemblies = { "BlitzLegacy.Core", "BlitzLegacy.Runtime", "BlitzLegacy.Network", "BlitzLegacy.Direct" };

    // Types a cheater would look for first: their own names may stay only
    // when Unity needs them (components), their members never.
    private static readonly Regex Sensitive = new Regex(
        "AntiCheat|AntiTamper|Reputation|SpamGuard|InputGuard|Signing|Integrity|Token|Secret|Ban\\b|" +
        "NetSession|NetOnlineService|BattleRemoteDriver|BattleNetPlan|AccountBook|AccountData|SaveService|" +
        "FileSaveStorage|Reward|Wallet|Currency|Shop|" +
        // offline-only cheats and the hard online bans (Sandbox, mod tanks)
        "Sandbox|Cheat|ModTank|StockTanks|OnlineBan|" +
        // the own authoritative server and its client: the whole wire protocol
        @"^BlitzLegacy\.Direct\.", RegexOptions.Compiled);

    // Names Unity calls by reflection on components.
    private static readonly HashSet<string> UnityMessages = new HashSet<string>
    {
        "Awake", "Start", "Update", "LateUpdate", "FixedUpdate", "OnEnable", "OnDisable", "OnDestroy",
        "OnApplicationQuit", "OnApplicationPause", "OnApplicationFocus", "OnGUI", "OnValidate", "Reset",
        "OnCollisionEnter", "OnCollisionStay", "OnCollisionExit", "OnTriggerEnter", "OnTriggerStay",
        "OnTriggerExit", "OnAudioFilterRead", "OnRenderObject", "OnWillRenderObject", "OnBecameVisible",
        "OnBecameInvisible", "OnRectTransformDimensionsChange", "OnTransformParentChanged",
        "OnBeforeTransformParentChanged", "OnCanvasGroupChanged", "OnDidApplyAnimationProperties",
        "OnParticleCollision", "OnPreRender", "OnPostRender", "OnRenderImage", "OnAnimatorMove",
        "OnControllerColliderHit", "OnJointBreak",
    };

    private static int Main(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: ObfuscationAudit MANAGED_DIR [--list]"); return 2; }
        var dir = args[0];
        var list = args.Contains("--list");
        var failed = false;
        foreach (var name in Assemblies)
        {
            var path = Path.Combine(dir, name + ".dll");
            if (!File.Exists(path)) { Console.WriteLine(name + ": MISSING"); failed = true; continue; }
            failed |= Audit(path, name, list);
        }
        Console.WriteLine(failed ? "RESULT: FAIL" : "RESULT: PASS");
        return failed ? 1 : 0;
    }

    private static bool Obfuscated(string s) => s.Any(c => c >= '\u3400' && c <= '\u9fff');

    private static bool Audit(string path, string asmName, bool list)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        int types = 0, typesHidden = 0, members = 0, membersHidden = 0;
        var leaks = new List<string>();
        var readable = new List<string>();

        // Names of types derived from Unity/Fusion bases (by base-type name).
        EntityHandle BaseOf(TypeDefinition t)
        {
            // UnityLinker output may carry a nil/odd extends column
            try { return t.BaseType; } catch (BadImageFormatException) { return default; }
        }
        string BaseName(TypeDefinition t)
        {
            var b = BaseOf(t);
            if (b.IsNil) { return ""; }
            switch (b.Kind)
            {
                case HandleKind.TypeReference: return md.GetString(md.GetTypeReference((TypeReferenceHandle)b).Name);
                case HandleKind.TypeDefinition: return md.GetString(md.GetTypeDefinition((TypeDefinitionHandle)b).Name);
                default: return "";
            }
        }
        var unityBases = new HashSet<string> { "MonoBehaviour", "ScriptableObject", "NetworkBehaviour", "SimulationBehaviour", "Graphic", "MaskableGraphic", "UIBehaviour", "Selectable", "Image", "Text", "ScrollRect", "BaseMeshEffect" };
        var componentCache = new Dictionary<TypeDefinitionHandle, bool>();
        bool IsComponent(TypeDefinitionHandle h)
        {
            if (componentCache.TryGetValue(h, out var v)) { return v; }
            componentCache[h] = false;
            var t = md.GetTypeDefinition(h);
            var r = unityBases.Contains(BaseName(t)) ||
                    (BaseOf(t).Kind == HandleKind.TypeDefinition && IsComponent((TypeDefinitionHandle)BaseOf(t)));
            componentCache[h] = r;
            return r;
        }

        foreach (var h in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(h);
            var tn = md.GetString(t.Name);
            var ns = md.GetString(t.Namespace);
            if (tn == "<Module>" || tn.StartsWith("<PrivateImplementationDetails")) { continue; }
            var declaring = t.GetDeclaringType();
            var full = (declaring.IsNil ? (ns.Length > 0 ? ns + "." : "") : FullName(md, declaring) + "/") + tn;
            var isEnum = BaseName(t) == "Enum";
            var component = IsComponent(h);
            var sensitive = Sensitive.IsMatch(full);
            // [RuntimeInitializeOnLoadMethod] is resolved by type + method name.
            var bootHook = t.GetMethods().Any(mh => HasAttr(md, md.GetMethodDefinition(mh).GetCustomAttributes(), "RuntimeInitializeOnLoadMethodAttribute"));
            types++;
            if (Obfuscated(tn) || tn.StartsWith("<")) { typesHidden++; }
            else
            {
                readable.Add("type " + full + (component ? " [component]" : isEnum ? " [enum]" : bootHook ? " [boot hook]" : ""));
                // enum names may surface through ToString(), kept by the rule file
                if (sensitive && !component && !isEnum && !bootHook) { leaks.Add("type " + full); }
            }

            void Member(string kind, string n, bool allowed)
            {
                members++;
                if (Obfuscated(n) || n.StartsWith("<") || n.StartsWith(".")) { membersHidden++; return; }
                readable.Add("  " + kind + " " + full + "::" + n + (allowed ? " [unity/override]" : ""));
                if (sensitive && !allowed && !isEnum) { leaks.Add(kind + " " + full + "::" + n); }
            }

            foreach (var mh in t.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                var n = md.GetString(m.Name);
                var a = m.Attributes;
                // virtual overrides/interface impls of external types keep their names
                var overrides = (a & MethodAttributes.Virtual) != 0 && (a & MethodAttributes.NewSlot) == 0;
                var iface = (a & MethodAttributes.Virtual) != 0 && (a & MethodAttributes.Final) != 0;
                var allowed = overrides || iface || (component && UnityMessages.Contains(n)) || n.Contains(".")
                              || HasAttr(md, m.GetCustomAttributes(), "RuntimeInitializeOnLoadMethodAttribute");
                Member("method", n, allowed);
            }
            foreach (var fh in t.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                var n = md.GetString(f.Name);
                var a = f.Attributes;
                // Unity serializes public / [SerializeField] fields of components and [Serializable] types
                var serial = component && (a & FieldAttributes.Static) == 0 && (a & FieldAttributes.NotSerialized) == 0 &&
                             ((a & FieldAttributes.FieldAccessMask) == FieldAttributes.Public || HasAttr(md, f.GetCustomAttributes(), "SerializeField"));
                Member("field", n, serial || isEnum || n == "value__");
            }
            foreach (var ph in t.GetProperties())
            {
                var p = md.GetPropertyDefinition(ph);
                Member("property", md.GetString(p.Name), HasAttr(md, p.GetCustomAttributes(), "NetworkedAttribute"));
            }
            foreach (var eh in t.GetEvents())
            {
                var e = md.GetEventDefinition(eh);
                Member("event", md.GetString(e.Name), false);
            }
        }

        Console.WriteLine($"{asmName}: types {typesHidden}/{types} renamed, members {membersHidden}/{members} renamed");
        if (list) { foreach (var r in readable) { Console.WriteLine("    " + r); } }
        foreach (var l in leaks) { Console.WriteLine("  SENSITIVE LEFT READABLE: " + l); }
        // Most members must be renamed; a mostly readable assembly means the
        // obfuscation pass did not run on it at all.
        var notRun = membersHidden * 2 < members;
        if (notRun) { Console.WriteLine("  NOT OBFUSCATED: most members keep their names"); }
        return leaks.Count > 0 || notRun;
    }

    private static string FullName(MetadataReader md, TypeDefinitionHandle h)
    {
        var t = md.GetTypeDefinition(h);
        var d = t.GetDeclaringType();
        var ns = md.GetString(t.Namespace);
        var n = md.GetString(t.Name);
        return d.IsNil ? (ns.Length > 0 ? ns + "." : "") + n : FullName(md, d) + "/" + n;
    }

    private static bool HasAttr(MetadataReader md, CustomAttributeHandleCollection attrs, string name)
    {
        foreach (var ah in attrs)
        {
            var a = md.GetCustomAttribute(ah);
            string n = null;
            if (a.Constructor.Kind == HandleKind.MemberReference)
            {
                var parent = md.GetMemberReference((MemberReferenceHandle)a.Constructor).Parent;
                if (parent.Kind == HandleKind.TypeReference) { n = md.GetString(md.GetTypeReference((TypeReferenceHandle)parent).Name); }
            }
            else if (a.Constructor.Kind == HandleKind.MethodDefinition)
            {
                n = md.GetString(md.GetTypeDefinition(md.GetMethodDefinition((MethodDefinitionHandle)a.Constructor).GetDeclaringType()).Name);
            }
            if (n != null && (n == name || n == name + "Attribute")) { return true; }
        }
        return false;
    }
}
