﻿using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

namespace QuickJS.Unity
{
    using UnityEngine;
    using UnityEditor;

    public partial class BindingManager
    {
        public DateTime dateTime;
        public TextGenerator log;
        public Prefs prefs;

        private HashSet<string> _blockedAssemblies = new HashSet<string>();  // 禁止导出的 assembly
        private List<string> _implicitAssemblies = new List<string>(); // 默认导出所有类型
        private List<string> _explicitAssemblies = new List<string>(); // 仅导出指定需要导出的类型

        private HashSet<Type> _blacklist;
        private List<string> _typePrefixBlacklist;
        private Dictionary<Type, TypeBindingInfo> _exportedTypes = new Dictionary<Type, TypeBindingInfo>();
        private Dictionary<Type, TSTypeNaming> _tsTypeNamings = new Dictionary<Type, TSTypeNaming>();
        private Dictionary<string, TSModuleBindingInfo> _exportedModules = new Dictionary<string, TSModuleBindingInfo>();
        private List<TypeBindingInfo> _collectedTypes = new List<TypeBindingInfo>(); // 已经完成导出的类型 
        private Dictionary<Type, DelegateBridgeBindingInfo> _exportedDelegates = new Dictionary<Type, DelegateBridgeBindingInfo>();
        private Dictionary<Type, Type> _redirectDelegates = new Dictionary<Type, Type>();

        private HashSet<Type> _hotfixTypes = new HashSet<Type>();
        private List<HotfixDelegateBindingInfo> _exportedHotfixDelegates = new List<HotfixDelegateBindingInfo>();
        // 类型修改
        private Dictionary<Type, TypeTransform> _typesTarnsform = new Dictionary<Type, TypeTransform>();
        private Dictionary<string, List<string>> _outputFiles = new Dictionary<string, List<string>>();
        private List<string> _removedFiles = new List<string>();

        private Dictionary<Type, List<string>> _tsTypeNameMap = new Dictionary<Type, List<string>>();
        private Dictionary<Type, string> _csTypeNameMap = new Dictionary<Type, string>();
        private Dictionary<Type, string> _csTypePusherMap = new Dictionary<Type, string>();
        private Dictionary<string, string> _csTypeNameMapS = new Dictionary<string, string>();
        private static HashSet<string> _tsKeywords = new HashSet<string>();

        // 自定义的处理流程
        private List<IBindingProcess> _bindingProcess = new List<IBindingProcess>();

        private IBindingCallback _callback;

        // ruleName: text => text
        private Dictionary<string, Func<string, string>> _nameRules = new Dictionary<string, Func<string, string>>();
        // text => text
        private Dictionary<string, string> _globalNameRules = new Dictionary<string, string>();

        public IBindingCallback callback => _callback;

        static BindingManager()
        {
            AddTSKeywords(
                // "type",
                "return", "function", "interface", "class", "let", "break", "as", "any", "switch", "case", "if", "enum",
                "throw", "else", "var", "number", "string", "get", "module", "instanceof", "typeof", "public", "private",
                "while", "void", "null", "super", "this", "new", "in", "await", "async", "extends", "static",
                "package", "implements", "interface", "continue", "yield", "const", "export", "finally", "for"
            );
        }

        public BindingManager(Prefs prefs, IBindingCallback callback)
        {
            this.prefs = prefs;
            this.dateTime = DateTime.Now;
            var tab = prefs.tab;
            var newline = prefs.newline;

            _callback = callback;
            _typePrefixBlacklist = new List<string>(prefs.typePrefixBlacklist);
            _blacklist = new HashSet<Type>();
            log = new TextGenerator(newline, tab);

            if (prefs.optToString)
            {
                AddGlobalNameRule("ToString", "toString");
            }

            AddNameRule("js", t => char.ToLower(t[0]) + t.Substring(1));
            SetAssemblyBlocked("ExCSS.Unity");
            AddTypePrefixBlacklist("System.SpanExtensions");

            HackGetComponents(TransformType(typeof(GameObject)))
                .WriteCSMethodBinding((bindPoint, cg, info) =>
                {
                    if (bindPoint == BindingPoints.METHOD_BINDING_BEFORE_INVOKE)
                    {
                        cg.cs.AppendLine("var inject = _js_game_object_add_component(ctx, argv[0], self, arg0);");
                        cg.cs.AppendLine("if (!inject.IsUndefined())");
                        using (cg.cs.CodeBlockScope())
                        {
                            cg.cs.AppendLine("return inject;");
                        }

                        return true;
                    }
                    return false;
                }, "AddComponent", typeof(Type));
            ;

            HackGetComponents(TransformType(typeof(Component)))
            ;

            TransformType(typeof(MonoBehaviour))
                .WriteCrossBindingConstructor();

            var buildTarget = EditorUserBuildSettings.activeBuildTarget;
            if (buildTarget != BuildTarget.iOS)
            {
                _typePrefixBlacklist.Add("UnityEngine.Apple");
            }
            if (buildTarget != BuildTarget.Android)
            {
                _typePrefixBlacklist.Add("UnityEngine.Android");
            }
            _typePrefixBlacklist.Add("SyntaxTree.");

            // fix d.ts, some C# classes use explicit implemented interface method
            SetTypeBlocked(typeof(UnityEngine.ILogHandler));
            SetTypeBlocked(typeof(UnityEngine.ISerializationCallbackReceiver));
            SetTypeBlocked(typeof(UnityEngine.Playables.ScriptPlayable<>));
            SetTypeBlocked(typeof(AOT.MonoPInvokeCallbackAttribute));

            TransformType(typeof(string))
                .AddTSMethodDeclaration("static Equals(a: string | Object, b: string | Object, comparisonType: any): boolean", "Equals", typeof(string), typeof(string), typeof(StringComparison))
                .AddTSMethodDeclaration("static Equals(a: string | Object, b: string | Object): boolean", "Equals", typeof(string), typeof(string))
            ;

            // SetTypeBlocked(typeof(RendererExtensions));
            SetTypeBlocked(typeof(UnityEngine.UI.ILayoutGroup));
            SetTypeBlocked(typeof(UnityEngine.UI.ILayoutSelfController));

            TransformType(typeof(UnityEngine.Events.UnityEvent<>))
                .Rename("UnityEvent1");

            TransformType(typeof(UnityEngine.Events.UnityEvent<,>))
                .Rename("UnityEvent2");

            TransformType(typeof(UnityEngine.Events.UnityEvent<,,>))
                .Rename("UnityEvent3");

            TransformType(typeof(UnityEngine.Events.UnityEvent<,,,>))
                .Rename("UnityEvent4");

            TransformType(typeof(UnityEngine.UI.PositionAsUV1))
                .SetMemberBlocked("ModifyMesh");
            TransformType(typeof(UnityEngine.UI.Shadow))
                .SetMemberBlocked("ModifyMesh");
            TransformType(typeof(UnityEngine.UI.Outline))
                .SetMemberBlocked("ModifyMesh");
            TransformType(typeof(UnityEngine.UI.Graphic))
                .SetMemberBlocked("OnRebuildRequested");
            TransformType(typeof(UnityEngine.Texture))
                .SetMemberBlocked("imageContentsHash");
            TransformType(typeof(UnityEngine.UI.Text))
                .SetMemberBlocked("OnRebuildRequested");
            TransformType(typeof(UnityEngine.Input))
                .SetMemberBlocked("IsJoystickPreconfigured"); // specific platform available only
            TransformType(typeof(UnityEngine.MonoBehaviour))
                .SetMemberBlocked("runInEditMode"); // editor only
            TransformType(typeof(UnityEngine.QualitySettings))
                .SetMemberBlocked("streamingMipmapsRenderersPerFrame");

            // editor 使用的 .net 与 player 所用存在差异, 这里屏蔽不存在的成员
            TransformType(typeof(double))
                .SetMemberBlocked("IsFinite")
            ;
            TransformType(typeof(float))
                .SetMemberBlocked("IsFinite")
            ;
            TransformType(typeof(string))
                .SetMemberBlocked("Chars")
            ;

            TransformType(typeof(Enum))
                .AddTSMethodDeclaration("static GetValues<T>(enumType: any): Array<T>", "GetValue", typeof(Type))
            ;

            // TransformType(typeof(Dictionary<,>))
            // ;

            TransformType(typeof(Array))
                .Rename("Array<T, RANK = 1>")

                .SetMethodBlocked("GetValue", typeof(long), typeof(long), typeof(long))
                .SetMethodBlocked("GetValue", typeof(long), typeof(long))
                .SetMethodBlocked("GetValue", typeof(long))
                .SetMethodBlocked("GetValue", typeof(long[]))
                .SetMethodBlocked("SetValue", typeof(object), typeof(long), typeof(long), typeof(long))
                .SetMethodBlocked("SetValue", typeof(object), typeof(long), typeof(long))
                .SetMethodBlocked("SetValue", typeof(object), typeof(long))
                .SetMethodBlocked("SetValue", typeof(object), typeof(long[]))
                .SetMethodBlocked("CopyTo", typeof(Array), typeof(long))
                .SetMethodBlocked("Copy", typeof(Array), typeof(long), typeof(Array), typeof(long), typeof(long))
                .SetMethodBlocked("Copy", typeof(Array), typeof(Array), typeof(long))
                .SetMethodBlocked("CreateInstance", typeof(Type), typeof(long[]))

                .AddTSMethodDeclaration("GetValue(index1: number, index2: number, index3: number): T", "GetValue", typeof(int), typeof(int), typeof(int))
                .AddTSMethodDeclaration("GetValue(index1: number, index2: number): T", "GetValue", typeof(int), typeof(int))
                .AddTSMethodDeclaration("GetValue(index: number): T", "GetValue", typeof(int))
                .AddTSMethodDeclaration("GetValue(...index: number[]): T", "GetValue", typeof(int[]))

                .AddTSMethodDeclaration("SetValue(value: T, index1: number, index2: number, index3: number): T", "SetValue", typeof(object), typeof(int), typeof(int), typeof(int))
                .AddTSMethodDeclaration("SetValue(value: T, index1: number, index2: number): T", "SetValue", typeof(object), typeof(int), typeof(int))
                .AddTSMethodDeclaration("SetValue(value: T, index: number): T", "SetValue", typeof(object), typeof(int))
                .AddTSMethodDeclaration("SetValue(value: T, ...index: number[]): T", "SetValue", typeof(object), typeof(int[]))

                .AddTSMethodDeclaration("static BinarySearch<T>(array: Array<T>, index: number, length: number, value: Object, comparer: any): number", "BinarySearch", typeof(Array), typeof(int), typeof(int), typeof(object), typeof(System.Collections.IComparer))
                .AddTSMethodDeclaration("static BinarySearch<T>(array: Array<T>, index: number, length: number, value: Object): number", "BinarySearch", typeof(Array), typeof(int), typeof(int), typeof(object))
                .AddTSMethodDeclaration("static BinarySearch<T>(array: Array<T>, value: Object, comparer: any): number", "BinarySearch", typeof(Array), typeof(object), typeof(System.Collections.IComparer))
                .AddTSMethodDeclaration("static BinarySearch<T>(array: Array<T>, value: Object): number", "BinarySearch", typeof(Array), typeof(object))
                .AddTSMethodDeclaration("static IndexOf<T>(array: Array<T>, value: Object, startIndex: number, count: number): number", "IndexOf", typeof(Array), typeof(object), typeof(int), typeof(int))
                .AddTSMethodDeclaration("static IndexOf<T>(array: Array<T>, value: Object, startIndex: number): number", "IndexOf", typeof(Array), typeof(object), typeof(int))
                .AddTSMethodDeclaration("static IndexOf<T>(array: Array<T>, value: Object): number", "IndexOf", typeof(Array), typeof(object))
                .AddTSMethodDeclaration("static LastIndexOf<T>(array: Array<T>, value: Object, startIndex: number, count: number): number", "LastIndexOf", typeof(Array), typeof(object), typeof(int), typeof(int))
                .AddTSMethodDeclaration("static LastIndexOf<T>(array: Array<T>, value: Object, startIndex: number): number", "LastIndexOf", typeof(Array), typeof(object), typeof(int))
                .AddTSMethodDeclaration("static LastIndexOf<T>(array: Array<T>, value: Object): number", "LastIndexOf", typeof(Array), typeof(object))
                .AddTSMethodDeclaration("static Reverse<T>(array: Array<T>, index: number, length: number): void", "Reverse", typeof(Array), typeof(int), typeof(int))
                .AddTSMethodDeclaration("static Reverse<T>(array: Array<T>): void", "Reverse", typeof(Array))
                .AddTSMethodDeclaration("static Sort<T>(keys: Array<T>, items: Array<T>, index: number, length: number, comparer: any): void", "Sort", typeof(Array), typeof(Array), typeof(int), typeof(int), typeof(System.Collections.IComparer))
                .AddTSMethodDeclaration("static Sort<T>(array: Array<T>, index: number, length: number, comparer: any): void", "Sort", typeof(Array), typeof(int), typeof(int), typeof(System.Collections.IComparer))
                .AddTSMethodDeclaration("static Sort<T>(keys: Array<T>, items: Array<T>, index: number, length: number): void", "Sort", typeof(Array), typeof(Array), typeof(int), typeof(int))
                .AddTSMethodDeclaration("static Sort<T>(array: Array<T>, index: number, length: number): void", "Sort", typeof(Array), typeof(int), typeof(int))
                .AddTSMethodDeclaration("static Sort<T>(keys: Array<T>, items: Array<T>, comparer: any): void", "Sort", typeof(Array), typeof(Array), typeof(System.Collections.IComparer))
                .AddTSMethodDeclaration("static Sort<T>(array: Array<T>, comparer: any): void", "Sort", typeof(Array), typeof(System.Collections.IComparer))
                .AddTSMethodDeclaration("static Sort<T>(keys: Array<T>, items: Array<T>): void", "Sort", typeof(Array), typeof(Array))
                .AddTSMethodDeclaration("static Sort<T>(array: Array<T>): void", "Sort", typeof(Array))
                .AddTSMethodDeclaration("static CreateInstance<T>(elementType: any, length1: number, length2: number, length3: number): Array<T>", "CreateInstance", typeof(Type), typeof(int), typeof(int), typeof(int))
                .AddTSMethodDeclaration("static CreateInstance<T>(elementType: any, length1: number, length2: number): Array<T>", "CreateInstance", typeof(Type), typeof(int), typeof(int))
                .AddTSMethodDeclaration("static CreateInstance<T>(elementType: any, lengths: Array<number>, lowerBounds: Array<number>): Array<T>", "CreateInstance", typeof(Type), typeof(int[]), typeof(int[]))
                .AddTSMethodDeclaration("static CreateInstance<T>(elementType: any, length: number): Array<T>", "CreateInstance", typeof(Type), typeof(int))
                .AddTSMethodDeclaration("static CreateInstance<T>(elementType: any, ...lengths: number[]): Array<T>", "CreateInstance", typeof(Type), typeof(int[]))
                .AddTSMethodDeclaration("static Clear<T>(array: Array<T>, index: number, length: number): void", "Clear", typeof(Array), typeof(int), typeof(int))
                .AddTSMethodDeclaration("static Copy<T>(sourceArray: Array<T>, sourceIndex: number, destinationArray: Array<T>, destinationIndex: number, length: number): void", "Copy", typeof(Array), typeof(int), typeof(Array), typeof(int), typeof(int))
                .AddTSMethodDeclaration("static Copy<T>(sourceArray: Array<T>, destinationArray: Array<T>, length: number): void", "Copy", typeof(Array), typeof(Array), typeof(int))
                .AddTSMethodDeclaration("static ConstrainedCopy<T>(sourceArray: Array<T>, sourceIndex: number, destinationArray: Array<T>, destinationIndex: number, length: number): void", "ConstrainedCopy", typeof(Array), typeof(int), typeof(Array), typeof(int), typeof(int))
            ;

            AddTSTypeNameMap(typeof(sbyte), "number");
            AddTSTypeNameMap(typeof(byte), $"{GetDefaultTypePrefix()}byte");
            AddTSTypeNameMap(typeof(int), "number");
            AddTSTypeNameMap(typeof(uint), "number");
            AddTSTypeNameMap(typeof(short), "number");
            AddTSTypeNameMap(typeof(ushort), "number");
            AddTSTypeNameMap(typeof(long), "number");
            AddTSTypeNameMap(typeof(ulong), "number");
            AddTSTypeNameMap(typeof(float), "number");
            AddTSTypeNameMap(typeof(double), "number");
            AddTSTypeNameMap(typeof(bool), "boolean");
            AddTSTypeNameMap(typeof(string), "string");
            AddTSTypeNameMap(typeof(char), "string");
            AddTSTypeNameMap(typeof(void), "void");

            TransformType(typeof(QuickJS.IO.ByteBuffer))
                .SetMemberBlocked("_SetPosition")
                .SetMethodBlocked("ReadAllBytes", typeof(IntPtr))
                .SetMethodBlocked("WriteBytes", typeof(IntPtr), typeof(int));

            AddCSTypeNameMap(typeof(sbyte), "sbyte");
            AddCSTypeNameMap(typeof(byte), "byte");
            AddCSTypeNameMap(typeof(int), "int");
            AddCSTypeNameMap(typeof(uint), "uint");
            AddCSTypeNameMap(typeof(short), "short");
            AddCSTypeNameMap(typeof(ushort), "ushort");
            AddCSTypeNameMap(typeof(long), "long");
            AddCSTypeNameMap(typeof(ulong), "ulong");
            AddCSTypeNameMap(typeof(float), "float");
            AddCSTypeNameMap(typeof(double), "double");
            AddCSTypeNameMap(typeof(bool), "bool");
            AddCSTypeNameMap(typeof(string), "string");
            AddCSTypeNameMap(typeof(char), "char");
            AddCSTypeNameMap(typeof(System.Object), "object");
            AddCSTypeNameMap(typeof(void), "void");

            // AddCSTypePusherMap(typeof(bool), "DuktapeDLL.duk_push_boolean");
            // AddCSTypePusherMap(typeof(char), "DuktapeDLL.duk_push_int");
            // AddCSTypePusherMap(typeof(byte), "DuktapeDLL.duk_push_int");
            // AddCSTypePusherMap(typeof(sbyte), "DuktapeDLL.duk_push_int");
            // AddCSTypePusherMap(typeof(short), "DuktapeDLL.duk_push_int");
            // AddCSTypePusherMap(typeof(ushort), "DuktapeDLL.duk_push_int");
            // AddCSTypePusherMap(typeof(int), "DuktapeDLL.duk_push_int");
            // AddCSTypePusherMap(typeof(uint), "DuktapeDLL.duk_push_uint");
            // AddCSTypePusherMap(typeof(long), "DuktapeDLL.duk_push_number");
            // AddCSTypePusherMap(typeof(ulong), "DuktapeDLL.duk_push_number");
            // AddCSTypePusherMap(typeof(float), "DuktapeDLL.duk_push_number");
            // AddCSTypePusherMap(typeof(double), "DuktapeDLL.duk_push_number");
            // AddCSTypePusherMap(typeof(string), "DuktapeDLL.duk_push_string");

            Initialize();
        }

        public void AddGlobalNameRule(string name, string mapping)
        {
            if (!string.IsNullOrEmpty(name))
            {
                _globalNameRules[name] = mapping;
            }
        }

        public void AddNameRule(string name, Func<string, string> fn)
        {
            if (!string.IsNullOrEmpty(name))
            {
                _nameRules[name] = fn;
            }
        }

        public string ApplyNameRule(string name, string text)
        {
            if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(name))
            {
                Func<string, string> tfn;
                if (_nameRules.TryGetValue(name, out tfn))
                {
                    return tfn(text);
                }
            }

            string mapping;
            if (_globalNameRules.TryGetValue(text, out mapping))
            {
                return mapping;
            }

            return text;
        }

        public void SetTypeBlocked(Type type)
        {
            _blacklist.Add(type);
        }

        public bool GetTSMethodDeclaration(MethodBase method, out string code)
        {
            var transform = GetTypeTransform(method.DeclaringType);
            if (transform != null)
            {
                return transform.GetTSMethodDeclaration(method, out code);
            }
            code = null;
            return false;
        }

        public bool GetTSMethodRename(MethodBase method, out string code)
        {
            var transform = GetTypeTransform(method.DeclaringType);
            if (transform != null)
            {
                return transform.GetTSMethodRename(method, out code);
            }
            code = null;
            return false;
        }

        public TypeTransform GetTypeTransform(Type type)
        {
            TypeTransform transform;
            return _typesTarnsform.TryGetValue(type, out transform) ? transform : null;
        }

        public TypeTransform TransformType(Type type)
        {
            TypeTransform transform;
            if (!_typesTarnsform.TryGetValue(type, out transform))
            {
                _typesTarnsform[type] = transform = new TypeTransform(type);
            }
            return transform;
        }

        private static bool _FindFilterBindingProcess(Type type, object l)
        {
            return type == typeof(IBindingProcess);
        }

        private void Initialize()
        {
            var assembly = Assembly.Load("Assembly-CSharp-Editor");
            var types = assembly.GetExportedTypes();
            for (int i = 0, size = types.Length; i < size; i++)
            {
                var type = types[i];
                if (type.IsAbstract)
                {
                    continue;
                }
                try
                {
                    var interfaces = type.FindInterfaces(_FindFilterBindingProcess, null);
                    if (interfaces != null && interfaces.Length > 0)
                    {
                        var ctor = type.GetConstructor(Type.EmptyTypes);
                        var inst = ctor.Invoke(null) as IBindingProcess;
                        var procName = inst.GetBindingProcessName();
                        if (procName == null || !prefs.skipExtras.Contains(procName))
                        {
                            inst.OnInitialize(this);
                            _bindingProcess.Add(inst);
                            Debug.Log($"add binding process: {type}");
                        }
                        else
                        {
                            Debug.Log($"skip binding process: {type}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"failed to add binding process: {type}\n{exception}");
                }
            }
        }

        /// <summary>
        /// TS: 添加保留字, CS中相关变量名等会自动重命名注册到js中
        /// </summary>
        public static void AddTSKeywords(params string[] keywords)
        {
            foreach (var keyword in keywords)
            {
                _tsKeywords.Add(keyword);
            }
        }

        /// <summary>
        /// 指定类型在 ts 声明中的映射名 (可以指定多项)
        /// </summary>
        public void AddTSTypeNameMap(Type type, params string[] names)
        {
            List<string> list;
            if (!_tsTypeNameMap.TryGetValue(type, out list))
            {
                _tsTypeNameMap[type] = list = new List<string>();
            }
            list.AddRange(names);
        }

        public bool GetTSTypeNameMap(Type type, out List<string> list)
        {
            return _tsTypeNameMap.TryGetValue(type, out list);
        }

        /// <summary>
        /// CS, 添加类型名称映射, 用于简化导出时的常用类型名
        /// </summary>
        public void AddCSTypeNameMap(Type type, string name)
        {
            _csTypeNameMap[type] = name;
            _csTypeNameMapS[type.FullName] = name;
            _csTypeNameMapS[GetCSNamespace(type) + type.Name] = name;
        }

        public void AddCSTypePusherMap(Type type, string name)
        {
            _csTypePusherMap[type] = name;
        }

        public void AddHotfixType(Type type)
        {
            if (!_hotfixTypes.Contains(type))
            {
                _hotfixTypes.Add(type);
            }
        }

        // 增加导出类型 (需要在 Collect 阶段进行)
        //NOTE: editor mscorlib 与 runtime 存在差异, 需要手工 block 差异
        public TypeTransform AddExportedType(Type type, bool importBaseType = false)
        {
            var typeTransform = TransformType(type);
            if (!_exportedTypes.ContainsKey(type))
            {
                var typeBindingInfo = new TypeBindingInfo(this, type, typeTransform);
                _exportedTypes.Add(type, typeBindingInfo);
                log.AppendLine($"AddExportedType: {type} Assembly: {type.Assembly} Location: {type.Assembly.Location}");

                var baseType = type.BaseType;
                if (baseType != null && !IsExportingBlocked(baseType))
                {
                    // 检查具体化泛型基类 (如果基类泛型定义在显式导出清单中, 那么导出此具体化类)
                    // Debug.LogFormat("{0} IsConstructedGenericType:{1} {2} {3}", type, type.IsConstructedGenericType, type.IsGenericType, importBaseType);
                    if (baseType.IsConstructedGenericType)
                    {
                        if (IsExportingExplicit(baseType.GetGenericTypeDefinition()))
                        {
                            AddExportedType(baseType);
                        }
                    }

                    if (importBaseType)
                    {
                        AddExportedType(baseType, importBaseType);
                    }
                }

                // 尝试以 typescript interface 形式导出泛型定义
                if (type.IsConstructedGenericType)
                {
                    AddExportedType(type.GetGenericTypeDefinition(), false);
                }
            }
            return typeTransform;
        }

        public TSModuleBindingInfo GetExportedModule(string name)
        {
            TSModuleBindingInfo module;
            if (!_exportedModules.TryGetValue(name, out module))
            {
                module = _exportedModules[name] = new TSModuleBindingInfo(name);
            }
            return module;
        }

        public bool RemoveExportedType(Type type)
        {
            return _exportedTypes.Remove(type);
        }

        public DelegateBridgeBindingInfo GetDelegateBindingInfo(Type type)
        {
            Type target;
            if (_redirectDelegates.TryGetValue(type, out target))
            {
                type = target;
            }
            DelegateBridgeBindingInfo delegateBindingInfo;
            if (_exportedDelegates.TryGetValue(type, out delegateBindingInfo))
            {
                return delegateBindingInfo;
            }
            return null;
        }

        public void CollectHotfix(Type type)
        {
            if (type == null)
            {
                return;
            }
            var transform = GetTypeTransform(type);
            var methodInfos = type.GetMethods(Binding.DynamicType.DefaultFlags);
            var hotfix = transform?.GetHotfix();
            var hotfixBefore = hotfix != null && (hotfix.flags & JSHotfixFlags.Before) != 0;
            var hotfixAfter = hotfix != null && (hotfix.flags & JSHotfixFlags.After) != 0;
            foreach (var methodInfo in methodInfos)
            {
                CollectHotfix(type, methodInfo, methodInfo.ReturnType);
                if (hotfixBefore | hotfixAfter)
                {
                    CollectHotfix(type, methodInfo, typeof(void));
                }
            }

            var constructorInfos = type.GetConstructors(Binding.DynamicType.DefaultFlags);
            foreach (var constructorInfo in constructorInfos)
            {
                CollectHotfix(type, constructorInfo, typeof(void));
            }
        }

        private bool CollectHotfix(Type declaringType, MethodBase methodBase, Type returnType)
        {
            if (methodBase.IsGenericMethodDefinition)
            {
                return false;
            }

            if (declaringType.IsValueType)
            {
                return false;
            }

            if (methodBase.Name == ".cctor")
            {
                return false;
            }

            var parameters = methodBase.GetParameters();

            for (var i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];

                // 暂不支持
                if (parameter.IsOut || parameter.ParameterType.IsPointer || parameter.IsDefined(typeof(ParamArrayAttribute)))
                {
                    return false;
                }
            }

            for (var i = 0; i < _exportedHotfixDelegates.Count; i++)
            {
                var regDelegateBinding = _exportedHotfixDelegates[i];
                if (regDelegateBinding.Equals(declaringType, methodBase.IsStatic, returnType, parameters))
                {
                    return true;
                }
            }

            var newDelegateBinding = new HotfixDelegateBindingInfo(declaringType, methodBase.IsStatic, returnType, parameters);
            _exportedHotfixDelegates.Add(newDelegateBinding);
            for (var i = 0; i < parameters.Length; i++)
            {
                CollectDelegate(parameters[i].ParameterType);
            }
            return true;
        }

        public void CollectTypeRequiredDefines(HashSet<string> defs, Type type)
        {
            if (type == null || type == typeof(void))
            {
                return;
            }

            defs.UnionWith(TransformType(type).requiredDefines);

            // check outter class for nested class
            CollectTypeRequiredDefines(defs, type.DeclaringType);
        }

        public void CollectTypeRequiredDefines(HashSet<string> defs, ParameterInfo[] parameters)
        {
            for (int i = 0, count = parameters.Length; i < count; i++)
            {
                var parameter = parameters[i];
                CollectTypeRequiredDefines(defs, parameter.ParameterType);
            }
        }

        // 收集所有 delegate 类型
        // delegateType: 委托本身的类型
        // explicitThis: 委托的首个参数作为 显式 this 传递
        public void CollectDelegate(Type delegateType)
        {
            if (delegateType == null || delegateType.BaseType != typeof(MulticastDelegate))
            {
                return;
            }
            if (!_exportedDelegates.ContainsKey(delegateType))
            {
                var invoke = delegateType.GetMethod("Invoke");
                var returnType = invoke.ReturnType;
                var parameters = invoke.GetParameters();
                if (ContainsPointer(invoke))
                {
                    log.AppendLine("skip unsafe (pointer) delegate: [{0}] {1}", delegateType, invoke);
                    return;
                }
                if (ContainsGenericParameters(invoke))
                {
                    log.AppendLine("skip generic delegate: [{0}] {1}", delegateType, invoke);
                    return;
                }
                if (prefs.skipDelegateWithByRefParams && ContainsByRefParameters(invoke))
                {
                    log.AppendLine("skip ByRef delegate (unsupported yet): [{0}] {1}", delegateType, invoke);
                    return;
                }
                var requiredDefines = new HashSet<string>();
                CollectTypeRequiredDefines(requiredDefines, delegateType);
                CollectTypeRequiredDefines(requiredDefines, returnType);
                CollectTypeRequiredDefines(requiredDefines, parameters);
                var defs = string.Join(" && ", from def in requiredDefines select def);

                // 是否存在等价 delegate
                foreach (var kv in _exportedDelegates)
                {
                    var regDelegateType = kv.Key;
                    var regDelegateBinding = kv.Value;
                    if (regDelegateBinding.Equals(returnType, parameters, defs))
                    {
                        log.AppendLine("skip delegate: {0} && {1} required defines: {2}", regDelegateBinding, delegateType, defs);
                        regDelegateBinding.types.Add(delegateType);
                        _redirectDelegates[delegateType] = regDelegateType;
                        return;
                    }
                }
                var delegateBindingInfo = new DelegateBridgeBindingInfo(returnType, parameters, defs);
                delegateBindingInfo.types.Add(delegateType);
                _exportedDelegates.Add(delegateType, delegateBindingInfo);
                log.AppendLine("add delegate: {0} required defines: {1}", delegateType, defs);
                for (var i = 0; i < parameters.Length; i++)
                {
                    CollectDelegate(parameters[i].ParameterType);
                }
            }
        }

        public bool IsExported(Type type)
        {
            return _exportedTypes.ContainsKey(type);
        }

        public string GetDefaultTypePrefix()
        {
            return "jsb.";
        }

        public string GetCSNamespace(Type type)
        {
            return string.IsNullOrEmpty(type.Namespace) ? "" : (type.Namespace + ".");
        }

        public string GetScriptObjectGetter(Type type, string ctx, string index, string varname)
        {
            var getter = GetScriptObjectPropertyGetter(type);
            return $"{getter}({ctx}, {index}, out {varname})";
        }

        private string GetScriptObjectPropertyGetter(Type type)
        {
            if (type.IsByRef)
            {
                return GetScriptObjectPropertyGetter(type.GetElementType());
            }
            if (type.IsArray && type.GetArrayRank() == 1)
            {
                var elementType = type.GetElementType();
                return GetScriptObjectPropertyGetter(elementType) + "_array";
            }
            if (type.IsValueType)
            {
                if (type.IsPrimitive)
                {
                    return "js_get_primitive";
                }
                if (type.IsEnum)
                {
                    return "js_get_enumvalue";
                }
                if (type.IsGenericType)
                {
                    if (type.GetGenericTypeDefinition() == typeof(Nullable<>))
                    {
                        var gArgs = type.GetGenericArguments();
                        if (gArgs[0].IsValueType && gArgs[0].IsPrimitive)
                        {
                            return "js_get_primitive";
                        }
                    }
                }
                return "js_get_structvalue";
            }
            if (type == typeof(string))
            {
                return "js_get_primitive";
            }
            if (type.BaseType == typeof(MulticastDelegate))
            {
                return "js_get_delegate";
            }
            if (type == typeof(Type))
            {
                return "js_get_type";
            }
            return "js_get_classvalue";
        }

        public string GetScriptObjectPusher(Type type, string ctx, string value)
        {
            string op;
            if (type.IsEnum)
            {
                var eType = type.GetEnumUnderlyingType();
                var eTypeName = this.GetCSTypeFullName(eType);
                return $"{this.GetScriptObjectPusher(eType, out op)}(ctx, ({eTypeName}){value})";
            }
            return $"{this.GetScriptObjectPusher(type, out op)}(ctx, {op}{value})";
        }

        public string GetScriptObjectPusher(Type type, out string op)
        {
            if (type.IsByRef)
            {
                return GetScriptObjectPusher(type.GetElementType(), out op);
            }

            op = "";
            string pusher;
            if (_csTypePusherMap.TryGetValue(type, out pusher))
            {
                return pusher;
            }

            if (type == typeof(Delegate) || type.BaseType == typeof(MulticastDelegate))
            {
                return "js_push_delegate";
            }

            if (type.IsValueType)
            {
                if (type.IsPrimitive)
                {
                    return "js_push_primitive";
                }

                if (type.IsEnum)
                {
                    return "js_push_enumvalue";
                }

                if (type.IsGenericType)
                {
                    if (type.GetGenericTypeDefinition() == typeof(Nullable<>))
                    {
                        var gArgs = type.GetGenericArguments();

                        if (gArgs[0].IsValueType && gArgs[0].IsPrimitive)
                        {
                            return "js_push_primitive";
                        }
                    }
                }

                op = "ref ";
                return "js_push_structvalue";
            }

            if (type == typeof(string))
            {
                return "js_push_primitive";
            }

            if (type == typeof(object))
            {
                return "js_push_var";
            }

            if (type == typeof(Type))
            {
                return "js_push_type";
            }

            return "js_push_classvalue";
        }

        public string GetTSVariable(string name)
        {
            if (_tsKeywords.Contains(name))
            {
                return name + "_";
            }
            return name;
        }

        public string GetTSVariable(ParameterInfo parameterInfo)
        {
            var name = parameterInfo.Name;
            return GetTSVariable(name);
        }

        // 保证生成一个以 prefix 为前缀, 与参数列表中所有参数名不同的名字
        public string GetUniqueName(ParameterInfo[] parameters, string prefix)
        {
            return GetUniqueName(parameters, prefix, 0);
        }

        public string GetUniqueName(ParameterInfo[] parameters, string prefix, int index)
        {
            var size = parameters.Length;
            var name = prefix + index;
            for (var i = 0; i < size; i++)
            {
                var parameter = parameters[i];
                if (parameter.Name == prefix)
                {
                    return GetUniqueName(parameters, prefix, index + 1);
                }
            }
            return name;
        }

        /// <summary>
        /// 获取父类的 TypeBindingInfo (沿继承链上溯直到存在导出), 不存在时返回 null
        /// </summary>
        public TypeBindingInfo GetSuperTypeBindingInfo(TypeBindingInfo typeBindingInfo)
        {
            var super = typeBindingInfo.super;
            while (super != null)
            {
                var superBindingInfo = GetExportedType(super);
                if (superBindingInfo != null)
                {
                    return superBindingInfo;
                }

                super = super.BaseType;
            }

            return null;
        }

        // 生成参数对应的字符串形式参数列表 (csharp)
        public string GetCSArglistDecl(ParameterInfo[] parameters)
        {
            var size = parameters.Length;
            var arglist = "";
            if (size == 0)
            {
                return arglist;
            }
            for (var i = 0; i < size; i++)
            {
                var parameter = parameters[i];
                var typename = GetCSTypeFullName(parameter.ParameterType);
                if (parameter.IsOut && parameter.ParameterType.IsByRef)
                {
                    arglist += "out ";
                }
                else if (parameter.ParameterType.IsByRef)
                {
                    arglist += "ref ";
                }
                arglist += typename;
                arglist += " ";
                arglist += parameter.Name;
                if (i != size - 1)
                {
                    arglist += ", ";
                }
            }
            return arglist;
        }

        // 获取 type 在 绑定代码 中对应类型名
        public string GetCSTypeFullName(Type type)
        {
            return GetCSTypeFullName(type, true);
        }

        public string GetCSTypeFullName(Type type, bool shortName)
        {
            // Debug.LogFormat("{0} Array {1} ByRef {2} GetElementType {3}", type, type.IsArray, type.IsByRef, type.GetElementType());
            if (type.IsGenericType)
            {
                var @namespace = string.Empty;
                var classname = type.Name.Substring(0, type.Name.Length - 2);
                if (type.IsNested)
                {
                    var indexOf = type.FullName.IndexOf("+");
                    @namespace = type.FullName.Substring(0, indexOf) + ".";
                }
                else
                {
                    @namespace = GetCSNamespace(type);
                }
                var purename = @namespace + classname;
                var gargs = type.GetGenericArguments();
                purename += "<";
                for (var i = 0; i < gargs.Length; i++)
                {
                    var garg = gargs[i];
                    purename += GetCSTypeFullName(garg, shortName);
                    if (i != gargs.Length - 1)
                    {
                        purename += ", ";
                    }
                }
                purename += ">";
                return purename;
            }

            if (type.IsArray)
            {
                var rank = type.GetArrayRank();
                var element = GetCSTypeFullName(type.GetElementType(), shortName);

                if (rank == 1)
                {
                    return element + "[]";
                }
                return element + "[" + StringRepeat(",", rank - 1) + "]";
            }

            if (type.IsByRef)
            {
                return GetCSTypeFullName(type.GetElementType(), shortName);
            }
            string name;
            if (shortName)
            {
                if (_csTypeNameMap.TryGetValue(type, out name))
                {
                    return name;
                }
            }
            if (type.IsGenericParameter)
            {
                return type.Name;
            }
            var fullname = type.FullName.Replace('+', '.');
            if (fullname.Contains("`"))
            {
                fullname = new Regex(@"`\d", RegexOptions.None).Replace(fullname, "");
                fullname = fullname.Replace("[", "<");
                fullname = fullname.Replace("]", ">");
            }
            if (_csTypeNameMapS.TryGetValue(fullname, out name))
            {
                return name;
            }
            return fullname;
        }

        public static string StringRepeat(string s, int repeat)
        {
            var sb = new System.Text.StringBuilder(s.Length * repeat);
            for (var i = 0; i < repeat; i++)
            {
                sb.Append(s);
            }
            return sb.ToString();
        }

        public string GetConstructorBindName()
        {
            if (prefs.randomizedBindingCode)
            {
                return (char)Random.Range('a', 'z') + Guid.NewGuid().ToString().Replace("-", "");
            }
            return "BindConstructor";
        }

        public string GetBindName(bool bStatic, string csName)
        {
            if (prefs.randomizedBindingCode)
            {
                return (char)Random.Range('A', 'Z') + Guid.NewGuid().ToString().Replace("-", "");
            }
            return (bStatic ? "BindStatic_" : "Bind_") + csName;
        }

        // 在 TypeTransform 准备完成后才有效
        public TSTypeNaming GetTSTypeNaming(Type type, bool noBindingRequired = false)
        {
            TSTypeNaming value = null;
            if (type != null && !_tsTypeNamings.TryGetValue(type, out value))
            {
                if (noBindingRequired || GetExportedType(type) != null)
                {
                    value = _tsTypeNamings[type] = new TSTypeNaming(this, type, GetTypeTransform(type));
                }
            }

            return value;
        }

        public string GetNamingAttribute(TypeTransform transform, MethodInfo info)
        {
            if (info.IsSpecialName)
            {
                switch (info.Name)
                {
                    case "get_Item": return "$GetValue";
                    case "set_Item": return "$SetValue";
                }
            }

            return ApplyNameRule(transform.GetNameRule(info), info.Name);
        }

        public string GetNamingAttribute(MemberInfo info)
        {
            return info.Name;
        }

        public TypeBindingInfo GetExportedType(Type type)
        {
            TypeBindingInfo typeBindingInfo;
            if (type != null && _exportedTypes.TryGetValue(type, out typeBindingInfo))
            {
                return typeBindingInfo;
            }
            return null;
        }

        // 是否在黑名单中屏蔽, 或者已知无需导出的类型
        public bool IsExportingBlocked(Type type)
        {
            if (_blacklist.Contains(type))
            {
                return true;
            }

            // if (type.IsGenericType && !type.IsConstructedGenericType)
            // {
            //     return true;
            // }

            if (type.Name.Contains("<"))
            {
                return true;
            }

            if (type.IsDefined(typeof(JSBindingAttribute), false))
            {
                return true;
            }

            if (type.BaseType == typeof(Attribute))
            {
                return true;
            }

            if (type.BaseType == typeof(MulticastDelegate))
            {
                return true;
            }

            if (type.IsPointer)
            {
                return true;
            }

            var encloser = type;
            while (encloser != null)
            {
                if (encloser.IsDefined(typeof(ObsoleteAttribute), false))
                {
                    return true;
                }
                encloser = encloser.DeclaringType;
            }

            for (int i = 0, size = _typePrefixBlacklist.Count; i < size; i++)
            {
                if (type.FullName.StartsWith(_typePrefixBlacklist[i]))
                {
                    return true;
                }
            }

            return false;
        }

        // 是否显式要求导出
        public bool IsExportingExplicit(Type type)
        {
            if (type.IsDefined(typeof(JSTypeAttribute), false))
            {
                return true;
            }
            return false;
        }

        private void OnPreCollectAssemblies()
        {
            for (int i = 0, size = _bindingProcess.Count; i < size; i++)
            {
                var bp = _bindingProcess[i];
                try
                {
                    bp.OnPreCollectAssemblies(this);
                }
                catch (Exception exception)
                {
                    this.Error($"process failed [{bp}][OnPreCollect]: {exception}");
                }
            }
        }

        private void OnPostCollectAssemblies()
        {
            for (int i = 0, size = _bindingProcess.Count; i < size; i++)
            {
                var bp = _bindingProcess[i];
                try
                {
                    bp.OnPostCollectAssemblies(this);
                }
                catch (Exception exception)
                {
                    this.Error($"process failed [{bp}][OnPostCollect]: {exception}");
                }
            }
        }

        private void OnPostExporting()
        {
            for (int i = 0, size = _bindingProcess.Count; i < size; i++)
            {
                var bp = _bindingProcess[i];
                try
                {
                    bp.OnPostExporting(this);
                }
                catch (Exception exception)
                {
                    this.Error($"process failed [{bp}][OnPostExporting]: {exception}");
                }
            }
        }

        private void OnPreExporting()
        {
            for (int i = 0, size = _bindingProcess.Count; i < size; i++)
            {
                var bp = _bindingProcess[i];
                try
                {
                    bp.OnPreExporting(this);
                }
                catch (Exception exception)
                {
                    this.Error($"process failed [{bp}][OnPreExporting]: {exception}");
                }
            }
        }

        private void OnPreCollectTypes()
        {
            for (int i = 0, size = _bindingProcess.Count; i < size; i++)
            {
                var bp = _bindingProcess[i];
                try
                {
                    bp.OnPreCollectTypes(this);
                }
                catch (Exception exception)
                {
                    this.Error($"process failed [{bp}][OnPreCollect]: {exception}");
                }
            }
        }

        private void OnPostCollectTypes()
        {
            for (int i = 0, size = _bindingProcess.Count; i < size; i++)
            {
                var bp = _bindingProcess[i];
                try
                {
                    bp.OnPostCollectTypes(this);
                }
                catch (Exception exception)
                {
                    this.Error($"process failed [{bp}][OnPostCollect]: {exception}");
                }
            }
        }

        private void OnPreGenerateType(TypeBindingInfo bindingInfo)
        {
            for (int i = 0, size = _bindingProcess.Count; i < size; i++)
            {
                var bp = _bindingProcess[i];
                try
                {
                    bp.OnPreGenerateType(this, bindingInfo);
                }
                catch (Exception exception)
                {
                    this.Error($"process failed [{bp}][OnPreGenerateType]: {exception}");
                }
            }
        }

        private void OnPostGenerateType(TypeBindingInfo bindingInfo)
        {
            for (int i = 0, size = _bindingProcess.Count; i < size; i++)
            {
                var bp = _bindingProcess[i];
                try
                {
                    bp.OnPostGenerateType(this, bindingInfo);
                }
                catch (Exception exception)
                {
                    this.Error($"process failed [{bp}][OnPostGenerateType]: {exception}");
                }
            }
        }

        public void OnPreGenerateDelegate(DelegateBridgeBindingInfo bindingInfo)
        {
            for (int i = 0, size = _bindingProcess.Count; i < size; i++)
            {
                var bp = _bindingProcess[i];
                try
                {
                    bp.OnPreGenerateDelegate(this, bindingInfo);
                }
                catch (Exception exception)
                {
                    this.Error($"process failed [{bp}][OnPreGenerateDelegate]: {exception}");
                }
            }
        }

        public void OnPostGenerateDelegate(DelegateBridgeBindingInfo bindingInfo)
        {
            for (int i = 0, size = _bindingProcess.Count; i < size; i++)
            {
                var bp = _bindingProcess[i];
                try
                {
                    bp.OnPostGenerateDelegate(this, bindingInfo);
                }
                catch (Exception exception)
                {
                    this.Error($"process failed [{bp}][OnPostGenerateDelegate]: {exception}");
                }
            }
        }

        private void OnCleanup()
        {
            for (int i = 0, size = _bindingProcess.Count; i < size; i++)
            {
                var bp = _bindingProcess[i];
                try
                {
                    bp.OnCleanup(this);
                }
                catch (Exception exception)
                {
                    this.Error($"process failed [{bp}][OnCleanup]: {exception}");
                }
            }
        }



        public void Collect()
        {
            // 收集直接类型, 加入 exportedTypes
            OnPreCollectAssemblies();
            AddAssemblies(false, prefs.explicitAssemblies.ToArray());
            AddAssemblies(true, prefs.implicitAssemblies.ToArray());
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                var assembly = assemblies[i];
                if (!assembly.IsDynamic && !IsAssemblyBlocked(assembly))
                {
                    AddAssemblies(false, assembly.FullName);
                }
            }
            OnPostCollectAssemblies();

            OnPreExporting();
            ExportAssemblies(_explicitAssemblies, false);
            ExportAssemblies(_implicitAssemblies, true);
            ExportBuiltins();
            OnPostExporting();

            log.AppendLine("collecting members");
            log.AddTabLevel();
            OnPreCollectTypes();
            foreach (var type in _hotfixTypes)
            {
                CollectHotfix(type);
            }

            foreach (var typeBindingInfoKV in _exportedTypes)
            {
                _CollectType(typeBindingInfoKV.Value.type);
            }

            OnPostCollectTypes();
            log.DecTabLevel();
        }

        private void _CollectType(Type type)
        {
            if (type == null)
            {
                return;
            }
            var typeBindingInfo = GetExportedType(type);

            _CollectType(type.DeclaringType);

            if (typeBindingInfo == null || _collectedTypes.Contains(typeBindingInfo))
            {
                return;
            }

            typeBindingInfo.Initialize();
            _collectedTypes.Add(typeBindingInfo);
            log.AppendLine("type: {0}", type);
            log.AddTabLevel();
            typeBindingInfo.Collect();
            log.DecTabLevel();
        }

        public void AddTypePrefixBlacklist(string prefix)
        {
            if (!_typePrefixBlacklist.Contains(prefix))
            {
                _typePrefixBlacklist.Add(prefix);
            }
        }

        public void SetAssemblyBlocked(string name)
        {
            _blockedAssemblies.Add(name);
        }

        public bool IsAssemblyBlocked(Assembly assembly)
        {
            try
            {
                if (string.IsNullOrEmpty(assembly.Location))
                {
                    return false;
                }
                var fileInfo = new FileInfo(assembly.Location);
                if (fileInfo.DirectoryName.EndsWith("/Editor/Data/Managed"))
                {
                    return true;
                }
                if (fileInfo.Name.StartsWith("UnityEditor"))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogErrorFormat("{0} {1} {2}", assembly, assembly.Location, ex);
                return false;
            }

            var refs = assembly.GetReferencedAssemblies();
            for (int i = 0, count = refs.Length; i < count; i++)
            {
                var @ref = refs[i];
                if (@ref.Name == "UnityEditor")
                {
                    return true;
                }
            }
            var comma = assembly.FullName.IndexOf(',');
            var name = comma >= 0 ? assembly.FullName.Substring(0, comma) : assembly.FullName;
            return _blockedAssemblies.Contains(name);
        }

        public void AddAssemblies(bool implicitExport, params string[] assemblyNames)
        {
            if (implicitExport)
            {
                for (var i = 0; i < assemblyNames.Length; i++)
                {
                    var assemblyName = assemblyNames[i];
                    if (!_implicitAssemblies.Contains(assemblyName) && !_explicitAssemblies.Contains(assemblyName))
                    {
                        _implicitAssemblies.Add(assemblyName);
                    }
                }
            }
            else
            {
                for (var i = 0; i < assemblyNames.Length; i++)
                {
                    var assemblyName = assemblyNames[i];
                    if (!_implicitAssemblies.Contains(assemblyName) && !_explicitAssemblies.Contains(assemblyName))
                    {
                        _explicitAssemblies.Add(assemblyName);
                    }
                }
            }
        }

        public void RemoveAssemblies(params string[] assemblyNames)
        {
            foreach (var name in assemblyNames)
            {
                _implicitAssemblies.Remove(name);
                _explicitAssemblies.Remove(name);
            }
        }

        /// <summary>
        /// 导出一些必要的基本类型
        /// </summary>
        private void ExportBuiltins()
        {
            AddExportedType(typeof(byte)).SystemRuntime();
            AddExportedType(typeof(sbyte)).SystemRuntime();
            AddExportedType(typeof(float)).SystemRuntime();
            AddExportedType(typeof(double)).SystemRuntime();
            AddExportedType(typeof(string)).SystemRuntime();
            AddExportedType(typeof(int)).SystemRuntime();
            AddExportedType(typeof(uint)).SystemRuntime();
            AddExportedType(typeof(short)).SystemRuntime();
            AddExportedType(typeof(ushort)).SystemRuntime();
            AddExportedType(typeof(object)).SystemRuntime();
            AddExportedType(typeof(Array)).SystemRuntime();
            AddExportedType(typeof(Delegate)).SystemRuntime()
                .SetMemberBlocked("CreateDelegate")
            ;

            AddExportedType(typeof(LayerMask));
            AddExportedType(typeof(Color));
            AddExportedType(typeof(Color32));
            AddExportedType(typeof(Vector2));
            AddExportedType(typeof(Vector2Int));
            AddExportedType(typeof(Vector3));
            AddExportedType(typeof(Vector3Int));
            AddExportedType(typeof(Vector4));
            AddExportedType(typeof(Quaternion));
            AddExportedType(typeof(Matrix4x4));
            AddExportedType(typeof(PrimitiveType));
            AddExportedType(typeof(Object))
                .EnableOperatorOverloading(false)
            ;
            AddExportedType(typeof(GameObject), true);
            AddExportedType(typeof(Camera), true);
            AddExportedType(typeof(Transform), true);
            AddExportedType(typeof(MonoBehaviour), true);

            AddExportedType(typeof(QuickJS.IO.ByteBuffer));
        }

        // implicitExport: 默认进行导出(黑名单例外), 否则根据导出标记或手工添加
        private void ExportAssemblies(List<string> assemblyNames, bool implicitExport)
        {
            foreach (var assemblyName in assemblyNames)
            {
                log.AppendLine("assembly: {0}", assemblyName);
                log.AddTabLevel();
                try
                {
                    var assembly = Assembly.Load(assemblyName);
                    var types = assembly.GetExportedTypes();

                    log.AppendLine("info: {0}", assembly);
                    log.AppendLine("location: {0}", assembly.Location);
                    log.AppendLine("types: {0}", types.Length);
                    log.AddTabLevel();
                    foreach (var type in types)
                    {
                        var hotfixTag = type.GetCustomAttribute(typeof(JSHotfixAttribute)) as JSHotfixAttribute;
                        if (hotfixTag != null)
                        {
                            TransformType(type).SetHotfix(hotfixTag);
                            AddHotfixType(type);
                        }

                        if (IsExportingBlocked(type))
                        {
                            log.AppendLine("blocked: {0}", type.FullName);
                            continue;
                        }

                        if (implicitExport)
                        {
                            log.AppendLine("export (implicit): {0}", type.FullName);
                            this.AddExportedType(type, true);
                            continue;
                        }

                        if (IsExportingExplicit(type))
                        {
                            log.AppendLine("export (explicit): {0}", type.FullName);
                            this.AddExportedType(type, true);
                            continue;
                        }

                        TryExportTypeMembers(type);
                        log.AppendLine("skip: {0}", type.FullName);
                    }
                    log.DecTabLevel();
                }
                catch (Exception exception)
                {
                    Debug.LogErrorFormat("{0}", exception);
                    log.AppendLine(exception.ToString());
                }
                log.DecTabLevel();
            }
        }

        // 此类本身不导出, 但可能包含扩展方法等
        private void TryExportTypeMembers(Type type)
        {
            var methods = type.GetMethods(QuickJS.Binding.DynamicType.PublicFlags);
            var methodCount = methods.Length;
            for (var methodIndex = 0; methodIndex < methodCount; methodIndex++)
            {
                var method = methods[methodIndex];
                if (IsExtensionMethod(method))
                {
                    var parameters = method.GetParameters();
                    var declType = parameters[0].ParameterType;
                    TransformType(declType).AddExtensionMethod(method);
                }
            }
        }

        // 清理多余文件
        public void Cleanup()
        {
            log.AppendLine("cleanup");
            log.AddTabLevel();
            Cleanup(_outputFiles, file =>
            {
                _removedFiles.Add(file);
                log.AppendLine("remove unused file {0}", file);
            });
            OnCleanup();
            log.DecTabLevel();
        }

        public static void Cleanup(Dictionary<string, List<string>> excludedFilesKV, Action<string> ondelete)
        {
            foreach (var kv in excludedFilesKV)
            {
                var outDir = kv.Key;
                var excludedFiles = kv.Value;
                if (Directory.Exists(outDir))
                {
                    foreach (var file in Directory.GetFiles(outDir))
                    {
                        var nfile = file;
                        if (file.EndsWith(".meta"))
                        {
                            nfile = file.Substring(0, file.Length - 5);
                        }
                        // Debug.LogFormat("checking file {0}", nfile);
                        if (excludedFiles == null || !excludedFiles.Contains(nfile))
                        {
                            File.Delete(file);
                            if (ondelete != null)
                            {
                                ondelete(file);
                            }
                        }
                    }
                }
            }
        }

        public void AddOutputFile(string outDir, string filename)
        {
            List<string> list;
            if (!_outputFiles.TryGetValue(outDir, out list))
            {
                list = _outputFiles[outDir] = new List<string>();
            }
            list.Add(filename);
        }

        public void Generate(TypeBindingFlags typeBindingFlags)
        {
            var cg = new CodeGenerator(this, typeBindingFlags);
            var csOutDir = prefs.procOutDir;
            var tsOutDir = prefs.procTypescriptDir;
            var extraExt = prefs.extraExt;
            // var extraExt = "";

            var cancel = false;
            var current = 0;
            var total = _exportedTypes.Count;

            cg.Begin();
            foreach (var typeKV in _exportedTypes)
            {
                var typeBindingInfo = typeKV.Value;
                try
                {
                    current++;
                    cancel = _callback.OnTypeGenerating(typeBindingInfo, current, total);
                    if (cancel)
                    {
                        Warn("operation canceled");
                        break;
                    }
                    if (!typeBindingInfo.omit)
                    {
                        cg.Clear();
                        OnPreGenerateType(typeBindingInfo);
                        cg.Generate(typeBindingInfo);
                        OnPostGenerateType(typeBindingInfo);
                        cg.WriteCSharp(csOutDir, typeBindingInfo.GetFileName(), extraExt);
                        cg.WriteTSD(tsOutDir, typeBindingInfo.GetFileName(), extraExt);
                    }
                }
                catch (Exception exception)
                {
                    Error($"generate failed {typeBindingInfo.type.FullName}: {exception.Message}");
                    Debug.LogError(exception.StackTrace);
                }
            }

            if (!cancel)
            {
                try
                {
                    var exportedDelegatesArray = new DelegateBridgeBindingInfo[this._exportedDelegates.Count];
                    this._exportedDelegates.Values.CopyTo(exportedDelegatesArray, 0);

                    cg.Clear();
                    cg.Generate(exportedDelegatesArray, _exportedHotfixDelegates);
                    cg.WriteCSharp(csOutDir, CodeGenerator.NameOfDelegates, extraExt);
                    cg.WriteTSD(tsOutDir, CodeGenerator.NameOfDelegates, extraExt);
                }
                catch (Exception exception)
                {
                    Error($"generate delegates failed: {exception.Message}");
                    Debug.LogError(exception.StackTrace);
                }
            }

            if (!cancel)
            {
                try
                {
                    cg.Clear();
                    cg.GenerateBindingList(_collectedTypes);
                    cg.WriteCSharp(csOutDir, CodeGenerator.NameOfBindingList, extraExt);
                    cg.WriteTSD(tsOutDir, CodeGenerator.NameOfBindingList, extraExt);
                }
                catch (Exception exception)
                {
                    Error($"generate delegates failed: {exception.Message}");
                    Debug.LogError(exception.StackTrace);
                }
            }
            cg.End();

            if (!cancel)
            {
                try
                {
                    cg.Clear();
                    cg.WriteTSD(tsOutDir, extraExt);
                }
                catch (Exception exception)
                {
                    Error($"generate delegates failed: {exception.Message}");
                    Debug.LogError(exception.StackTrace);
                }
            }

            try
            {
                if (!string.IsNullOrEmpty(prefs.logPath))
                {
                    var logPath = prefs.logPath;
                    var logDir = Path.GetDirectoryName(logPath);
                    if (!Directory.Exists(logDir))
                    {
                        Directory.CreateDirectory(logDir);
                    }
                    File.WriteAllText(logPath, log.Submit());
                }
            }
            catch (Exception)
            {
            }

            _callback.OnGenerateFinish();
        }

        public void Report()
        {
            var now = DateTime.Now;
            var ts = now.Subtract(dateTime);
            Debug.LogFormat("generated {0} type(s), {1} delegate(s), {2} deletion(s) in {3:0.##} seconds.",
                _exportedTypes.Count,
                _exportedDelegates.Count,
                _removedFiles.Count,
                ts.TotalSeconds);
        }
    }
}