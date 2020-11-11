namespace Fable.Cli

open System

type RunProcess(exeFile: string, args: string list, ?watch: bool, ?fast: bool) =
    member _.ExeFile = exeFile
    member _.Args = args
    member _.IsWatch = defaultArg watch false
    member _.IsFast = defaultArg fast false

type CliArgs =
    { ProjectFile: string
      RootDir: string
      OutDir: string option
      FableLibraryPath: string option
      ForcePkgs: bool
      NoRestore: bool
      Exclude: string option
      Replace: Map<string, string>
      RunProcess: RunProcess option
      CompilerOptions: Fable.CompilerOptions
      DeduplicateDic: Collections.Generic.Dictionary<string, string> }

type private TypeInThisAssembly = class end

[<RequireQualifiedAccess>]
module Log =
    let mutable private verbosity = Fable.Verbosity.Normal

    /// To be called only at the beginning of the app
    let makeVerbose() =
        verbosity <- Fable.Verbosity.Verbose

    let writerLock = obj()

    let always (msg: string) =
        if verbosity <> Fable.Verbosity.Silent
            && not(String.IsNullOrEmpty(msg)) then
//            lock writerLock <| fun () ->
                Console.Out.WriteLine(msg)
                // Console.Out.Flush()

    let verbose (msg: Lazy<string>) =
        if verbosity = Fable.Verbosity.Verbose then
            always msg.Value

module File =
    open System.IO

    /// File.ReadAllText fails with locked files. See https://stackoverflow.com/a/1389172
    let readAllTextNonBlocking (path: string) =
        if File.Exists(path) then
            use fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            use textReader = new StreamReader(fileStream)
            textReader.ReadToEnd()
        else
            Log.always("File does not exist: " + path)
            ""

    let getRelativePathFromCwd (path: string) =
        Path.GetRelativePath(Directory.GetCurrentDirectory(), path)

    let rec tryFindPackageJsonDir dir =
        if File.Exists(Path.Combine(dir, "package.json")) then Some dir
        else
            let parent = Directory.GetParent(dir)
            if isNull parent then None
            else tryFindPackageJsonDir parent.FullName

    let tryNodeModulesBin workingDir exeFile =
        tryFindPackageJsonDir workingDir
        |> Option.bind (fun pkgJsonDir ->
            let nodeModulesBin = Path.Join(pkgJsonDir, "node_modules", ".bin", exeFile)
            if File.Exists(nodeModulesBin) then Some nodeModulesBin
            else None)

[<RequireQualifiedAccess>]
module Process =
    open System.Runtime
    open System.Diagnostics

    let isWindows() =
        InteropServices.RuntimeInformation.IsOSPlatform(InteropServices.OSPlatform.Windows)

    let getCurrentAssembly() =
        typeof<TypeInThisAssembly>.Assembly

    let addToPath (dir: string) =
        let currentPath = Environment.GetEnvironmentVariable("PATH")
        IO.Path.GetFullPath(dir) + (if isWindows() then ";" else ":") + currentPath

    // Adapted from https://github.com/enricosada/dotnet-proj-info/blob/1e6d0521f7f333df7eff3148465f7df6191e0201/src/dotnet-proj/Program.fs#L155
    let private startProcess workingDir exePath args =
        let args = String.concat " " args
        let exePath, args =
            if isWindows() then "cmd", ("/C \"" + exePath + "\" " + args)
            else exePath, args

        Log.always(File.getRelativePathFromCwd(workingDir) + "> " + exePath + " " + args)

        let psi = ProcessStartInfo()
        // for envVar in envVars do
        //     psi.EnvironmentVariables.[envVar.Key] <- envVar.Value
        psi.FileName <- exePath
        psi.WorkingDirectory <- workingDir
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.Arguments <- args
        psi.CreateNoWindow <- true
        psi.UseShellExecute <- false

        let p = Process.Start(psi)
        p.OutputDataReceived.Add(fun ea -> Log.always(ea.Data))
        p.ErrorDataReceived.Add(fun ea -> Log.always(ea.Data))
        p.BeginOutputReadLine()
        p.BeginErrorReadLine()
        p

    let kill(p: Process) =
        p.Refresh()
        if not p.HasExited then
            p.Kill(entireProcessTree=true)

    let start =
        let mutable runningProcess = None

        // In Windows, terminating the main process doesn't kill the spawned ones so we need
        // to listen for the Console.CancelKeyPress and AssemblyLoadContext.Unloading events
        if isWindows() then
            Console.CancelKeyPress.AddHandler(ConsoleCancelEventHandler(fun _ _ ->
                runningProcess |> Option.iter kill))
            let assemblyLoadContext =
                getCurrentAssembly()
                |> Loader.AssemblyLoadContext.GetLoadContext
            assemblyLoadContext.add_Unloading(fun _ ->
                runningProcess |> Option.iter kill)

        fun (workingDir: string) (exePath: string) (args: string list) ->
            try
                runningProcess |> Option.iter kill
                let p = startProcess workingDir exePath args
                runningProcess <- Some p
            with ex ->
                Log.always("Cannot run: " + ex.Message)

    let runSync (workingDir: string) (exePath: string) (args: string list) =
        try
            let p = startProcess workingDir exePath args
            p.WaitForExit()
            p.ExitCode
        with ex ->
            Log.always("Cannot run: " + ex.Message)
            Log.always(ex.StackTrace)
            -1

[<RequireQualifiedAccess>]
module Async =
    let fold f (state: 'State) (xs: 'T seq) = async {
        let mutable state = state
        for x in xs do
            let! result = f state x
            state <- result
        return state
    }

    let map f x = async {
        let! x = x
        return f x
    }

    let tryPick (f: 'T->Async<'Result option>) xs: Async<'Result option> = async {
        let mutable result: 'Result option = None
        for x in xs do
            match result with
            | Some _ -> ()
            | None ->
                let! r = f x
                result <- r
        return result
    }

    let orElse (f: unit->Async<'T>) (x: Async<'T option>): Async<'T> = async {
        let! x = x
        match x with
        | Some x -> return x
        | None -> return! f ()
    }

    let AwaitObservable (obs: IObservable<'T>) =
        Async.FromContinuations(fun (onSuccess, _onError, _onCancel) ->
            let mutable disp = Unchecked.defaultof<IDisposable>
            disp <- obs.Subscribe(fun v ->
                disp.Dispose()
                onSuccess(v)))

module Imports =
    open Fable

    let trimPath (path: string) = path.Replace("../", "").Replace("./", "").Replace(":", "")
    let isRelativePath (path: string) = path.StartsWith("./") || path.StartsWith("../")
    let isAbsolutePath (path: string) = path.StartsWith('/') || path.IndexOf(':') = 1

    let getRelativePath (path: string) (pathTo: string) =
        let relPath = IO.Path.GetRelativePath(path, pathTo).Replace('\\', '/')
        if isRelativePath relPath then relPath else "./" + relPath

    let getTargetAbsolutePath (deduplicateDic: Collections.Generic.Dictionary<_,_>) importPath projDir outDir =
        let importPath = Path.normalizePath importPath
        let outDir = Path.normalizePath outDir
        // It may happen the importPath is already in outDir,
        // for example package sources in .fable folder
        if importPath.StartsWith(outDir) then importPath
        else
            let importDir = Path.GetDirectoryName(importPath)
            let importFile = Path.GetFileName(importPath)
            match deduplicateDic.TryGetValue(importDir) with
            | true, targetDir -> Path.Combine(targetDir, importFile)
            | false, _ ->
                let relDir = getRelativePath projDir importDir |> trimPath
                let targetDir =
                    Path.Combine(outDir, relDir)
                    |> Naming.preventConflicts deduplicateDic.ContainsValue
                deduplicateDic.Add(importDir, targetDir)
                Path.Combine(targetDir, importFile)

    let getTargetRelativePath deduplicateDic (importPath: string) targetDir projDir (outDir: string) =
        let absPath = getTargetAbsolutePath deduplicateDic importPath projDir outDir
        let relPath = getRelativePath targetDir absPath
        if isRelativePath relPath then relPath else "./" + relPath

    let getImportPath deduplicateDic sourcePath targetPath projDir outDir (importPath: string) =
        match outDir with
        | None -> importPath.Replace("${outDir}", ".")
        | Some outDir ->
            let importPath =
                if importPath.StartsWith("${outDir}")
                // NOTE: Path.Combine in Fable Prelude trims / at the start
                // of the 2nd argument, unline .NET IO.Path.Combine
                then Path.Combine(outDir, importPath.Replace("${outDir}", ""))
                else importPath
            let sourceDir = Path.GetDirectoryName(sourcePath)
            let targetDir = Path.GetDirectoryName(targetPath)
            let importPath =
                if isRelativePath importPath
                then Path.Combine(sourceDir, importPath) |> Path.normalizeFullPath
                else importPath
            if isAbsolutePath importPath then
                if importPath.EndsWith(".fs")
                then getTargetRelativePath deduplicateDic importPath targetDir projDir outDir
                else getRelativePath targetDir importPath
            else importPath

module Observable =
    type Observer<'T>(f) =
        interface IObserver<'T> with
            member _.OnNext v = f v
            member _.OnError _ = ()
            member _.OnCompleted() = ()

    type SingleObservable<'T>(dispose: unit -> unit) =
        let mutable listener: IObserver<'T> option = None
        member _.Trigger v =
            match listener with
            | Some lis -> lis.OnNext v
            | None -> ()
        interface IObservable<'T> with
            member _.Subscribe w =
                listener <- Some w
                { new IDisposable with
                    member _.Dispose() = dispose() }

    let throttle ms (obs: IObservable<'T>) =
        { new IObservable<Set<'T>> with
            member _.Subscribe w =
                let events = Collections.Generic.HashSet<'T>()
                let timer = new Timers.Timer(ms, AutoReset=false)
                timer.Elapsed.Add(fun _ ->
                    set events |> w.OnNext
                    timer.Dispose())
                let disp = obs.Subscribe(Observer(fun v ->
                    if events.Add(v) then
                        timer.Stop()
                        timer.Start()))
                { new IDisposable with
                    member _.Dispose() = disp.Dispose() } }
