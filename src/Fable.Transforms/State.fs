module Fable.Transforms.State

open Fable
open Fable.AST
open System.Collections.Generic
open FSharp.Compiler.SourceCodeServices

#if FABLE_COMPILER
type Dictionary<'TKey, 'TValue> with
    member x.GetOrAdd (key, valueFactory) =
        match x.TryGetValue key with
        | true, v -> v
        | false, _ -> let v = valueFactory(key) in x.Add(key, v); v
    member x.AddOrUpdate (key, valueFactory, updateFactory) =
        if x.ContainsKey(key)
        then let v = updateFactory key x.[key] in x.[key] <- v; v
        else let v = valueFactory(key) in x.Add(key, v); v
    static member From<'K, 'V when 'K : equality>(kvs: KeyValuePair<'K, 'V> seq) =
        let d = Dictionary()
        for kv in kvs do
            d.Add(kv.Key, kv.Value)
        d

type ConcurrentDictionary<'TKey, 'TValue> = Dictionary<'TKey, 'TValue>
#else
open System.Collections.Concurrent
type ConcurrentDictionary<'TKey, 'TValue> with
    static member From(kvs: KeyValuePair<'TKey, 'TValue> seq) =
        ConcurrentDictionary(kvs)
#endif

type Package =
    { Id: string
      Version: string
      FsprojPath: string
      DllPath: string
      SourcePaths: string list
      Dependencies: Set<string> }

type PluginRef =
    { DllPath: string
      TypeFullName: string }

let private entityRefToString (r: Fable.EntityRef) =
    match r.SourcePath with
    | Some x -> x + "::" + r.QualifiedName
    | None -> r.QualifiedName

let rec private withNestedEntities useSourcePath (e: FSharpEntity) =
    if e.IsFSharpAbbreviation then Seq.empty
    else
        seq  {
            let sourcePath = if useSourcePath then Some(FSharp2Fable.FsEnt.SourcePath e) else None
            yield ({ QualifiedName = FSharp2Fable.FsEnt.QualifiedName e
                     SourcePath = sourcePath }: Fable.EntityRef), e
            for sub in e.NestedEntities do
                yield! withNestedEntities useSourcePath sub
        }

let private loadDllEntitiesAndPlugins getPlugin (checkResults: FSharpCheckProjectResults) =
    let plugins = Dictionary<Fable.EntityRef, System.Type>()

    // printfn "MEMORY %i" (System.GC.GetTotalMemory(true))

    let dllEntities =
        checkResults.ProjectContext.GetReferencedAssemblies()
        |> Seq.collect (fun a ->
            let entities =
                a.Contents.Entities
                |> Seq.collect (withNestedEntities false)

            match a.FileName with
            | Some dllPath when not(dllPath.EndsWith("Fable.AST.dll")) ->
                entities |> Seq.map (fun (r, e) ->
                    try
                        if e.IsAttributeType
                            && FSharp2Fable.Util.inherits e "Fable.PluginAttribute" then
                            let plugin = getPlugin { DllPath = dllPath; TypeFullName = e.FullName }
                            plugins.Add(r, plugin)
                    with _ -> ()
                    r, e)
            | _ -> entities)
        |> Seq.map (fun (r, e) -> entityRefToString r, FSharp2Fable.FsEnt e :> Fable.Entity)
        |> Map

    // printfn "MEMORY %i" (System.GC.GetTotalMemory(true))

    let plugins =
        ({ MemberDeclarationPlugins = Map.empty }, plugins)
        ||> Seq.fold (fun acc kv ->
            if kv.Value.IsSubclassOf(typeof<MemberDeclarationPluginAttribute>) then
                { acc with MemberDeclarationPlugins = Map.add kv.Key kv.Value acc.MemberDeclarationPlugins }
            else acc)

    dllEntities, plugins

type Project(checkResults: FSharpCheckProjectResults,
             ?getPlugin: PluginRef -> System.Type,
             ?optimizeFSharpAst,
             ?dllEntitiesAndPlugins) =

    let optimizeFSharpAst = defaultArg optimizeFSharpAst false
    let getPlugin = defaultArg getPlugin (fun _ -> failwith "Plugins are not supported")
    let dllEntities, plugins =
        match dllEntitiesAndPlugins with
        | Some(dllEntities, plugins) -> dllEntities, plugins
        | None -> loadDllEntitiesAndPlugins getPlugin checkResults

    let inlineExprs = ConcurrentDictionary<string, InlineExpr>()

    let implFiles =
        (if optimizeFSharpAst then checkResults.GetOptimizedAssemblyContents().ImplementationFiles
         else checkResults.AssemblyContents.ImplementationFiles)
        |> Seq.map (fun file -> Path.normalizePathAndEnsureFsExtension file.FileName, file)
        |> dict

    // TODO: Save root modules of unchanged files from previous compilation?
    let rootModules =
        implFiles |> Seq.map (fun kv ->
            kv.Key, FSharp2Fable.Compiler.getRootModule kv.Value) |> dict

    let entities =
        // Read entities in current project
        checkResults.AssemblyContents.ImplementationFiles
        |> Seq.collect (fun file ->
            FSharp2Fable.Compiler.getRootFSharpEntities file
            |> Seq.collect (withNestedEntities true))
        |> Seq.fold (fun entities (r, e) ->
            let r = entityRefToString r
            let e = FSharp2Fable.FsEnt e :> Fable.Entity
            Map.add r e entities) dllEntities

    member _.Update(checkResults) =
        Project(checkResults,
                optimizeFSharpAst=optimizeFSharpAst,
                dllEntitiesAndPlugins=(dllEntities, plugins))

    member _.Plugins = plugins
    member _.ImplementationFiles = implFiles
    member _.RootModules = rootModules
    member _.Entities = entities
    member _.InlineExprs = inlineExprs
    member _.Errors = checkResults.Errors

type Log =
    { Message: string
      Tag: string
      Severity: Severity
      Range: SourceLocation option
      FileName: string option }

    static member Make(severity, msg, ?fileName, ?range, ?tag) =
        { Message = msg
          Tag = defaultArg tag "FABLE"
          Severity = severity
          Range = range
          FileName = fileName }

    static member MakeError(msg, ?fileName, ?range, ?tag) =
        Log.Make(Severity.Error, msg, ?fileName=fileName, ?range=range, ?tag=tag)

/// Type with utilities for compiling F# files to JS
/// Not thread-safe, an instance must be created per file
type CompilerImpl(currentFile, project: Project, options, fableLibraryDir: string) =
    let logs = ResizeArray<Log>()
    let watchDependencies = HashSet<string>()
    let fableLibraryDir = fableLibraryDir.TrimEnd('/')

    member _.Options = options
    member _.CurrentFile = currentFile
    member _.Logs = logs.ToArray()
    member _.WatchDependencies = Array.ofSeq watchDependencies

    interface Compiler with
        member _.Options = options
        member _.Plugins = project.Plugins
        member _.LibraryDir = fableLibraryDir
        member _.CurrentFile = currentFile

        member _.GetImplementationFile(fileName) =
            let fileName = Path.normalizePathAndEnsureFsExtension fileName
            match project.ImplementationFiles.TryGetValue(fileName) with
            | true, rootModule -> rootModule
            | false, _ -> failwith ("Cannot find implementation file " + fileName)

        member this.GetRootModule(fileName) =
            let fileName = Path.normalizePathAndEnsureFsExtension fileName
            match project.RootModules.TryGetValue(fileName) with
            | true, rootModule -> rootModule
            | false, _ ->
                let msg = sprintf "Cannot find root module for %s. If this belongs to a package, make sure it includes the source files." fileName
                (this :> Compiler).AddLog(msg, Severity.Warning, fileName=currentFile)
                "" // failwith msg

        member _.GetEntity(entityRef) =
            let fullName = entityRefToString entityRef
            match Map.tryFind fullName project.Entities with
            | Some e -> e
            | None -> failwithf "Cannot find entity %s" fullName

        member _.GetOrAddInlineExpr(fullName, generate) =
            project.InlineExprs.GetOrAdd(fullName, fun _ -> generate())

        member _.AddWatchDependency(file) =
            if file <> currentFile then
                watchDependencies.Add(file) |> ignore

        member _.AddLog(msg, severity, ?range, ?fileName:string, ?tag: string) =
            Log.Make(severity, msg, ?range=range, ?fileName=fileName, ?tag=tag)
            |> logs.Add
