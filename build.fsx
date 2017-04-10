// --------------------------------------------------------------------------------------
// FAKE build script
// --------------------------------------------------------------------------------------

#r @"packages/build/FAKE/tools/FakeLib.dll"
#r "System.IO.Compression.FileSystem"

open Fake
open Fake.Git
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper
open Fake.UserInputHelper
open System
open System.IO
open Fake.Testing.NUnit3

// --------------------------------------------------------------------------------------
// START TODO: Provide project-specific details below
// --------------------------------------------------------------------------------------

// Information about the project are used
//  - for version and project name in generated AssemblyInfo file
//  - by the generated NuGet package
//  - to run tests and to publish documentation on GitHub gh-pages
//  - for documentation, you also need to edit info in "docs/tools/generate.fsx"

// The name of the project
// (used by attributes in AssemblyInfo, name of a NuGet package and directory in 'src')
let project = "Paket"

// Short summary of the project
// (used as description in AssemblyInfo and as a short summary for NuGet package)
let summary = "A dependency manager for .NET with support for NuGet packages and git repositories."

// Longer description of the project
// (used as a description for NuGet package; line breaks are automatically cleaned up)
let description = "A dependency manager for .NET with support for NuGet packages and git repositories."

// List of author names (for NuGet package)
let authors = [ "Paket team" ]

// Tags for your project (for NuGet package)
let tags = "nuget, bundler, F#"

// File system information
let solutionFile  = "Paket.sln"
let solutionFilePowerShell = "Paket.PowerShell.sln"

// Pattern specifying assemblies to be tested using NUnit
let testAssemblies = "tests/**/bin/Release/*Tests*.dll"
let integrationTestAssemblies = "integrationtests/**/bin/Release/*Tests*.dll"

// Git configuration (used for publishing documentation in gh-pages branch)
// The profile where the project is posted
let gitOwner = "fsprojects"
let gitHome = "https://github.com/" + gitOwner

// The name of the project on GitHub
let gitName = "Paket"

// The url for the raw files hosted
let gitRaw = environVarOrDefault "gitRaw" "https://raw.github.com/fsprojects"

let dotnetcliVersion = "1.0.0-preview3-004056"

let dotnetCliPath = DirectoryInfo "./dotnetcore"

let netcoreFiles = !! "src/**.preview?/*.fsproj" |> Seq.toList

// --------------------------------------------------------------------------------------
// END TODO: The rest of the file includes standard build steps
// --------------------------------------------------------------------------------------

let buildDir = "bin"
let tempDir = "temp"
let buildMergedDir = buildDir @@ "merged"
let buildMergedDirPS = buildDir @@ "Paket.PowerShell"

Environment.CurrentDirectory <- __SOURCE_DIRECTORY__
// Read additional information from the release notes document
let releaseNotesData = 
    File.ReadAllLines "RELEASE_NOTES.md"
    |> parseAllReleaseNotes

let release = List.head releaseNotesData

let stable = 
    match releaseNotesData |> List.tryFind (fun r -> r.NugetVersion.Contains("-") |> not) with
    | Some stable -> stable
    | _ -> release

let genFSAssemblyInfo (projectPath) =
    let projectName = System.IO.Path.GetFileNameWithoutExtension(projectPath)
    let folderName = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(projectPath))
    let basePath = "src" @@ folderName
    let fileName = basePath @@ "AssemblyInfo.fs"
    CreateFSharpAssemblyInfo fileName
      [ Attribute.Title (projectName)
        Attribute.Product project
        Attribute.Company (authors |> String.concat ", ")
        Attribute.Description summary
        Attribute.Version release.AssemblyVersion
        Attribute.FileVersion release.AssemblyVersion
        Attribute.InformationalVersion release.NugetVersion ]

let genCSAssemblyInfo (projectPath) =
    let projectName = System.IO.Path.GetFileNameWithoutExtension(projectPath)
    let folderName = System.IO.Path.GetDirectoryName(projectPath)
    let basePath = folderName @@ "Properties"
    let fileName = basePath @@ "AssemblyInfo.cs"
    CreateCSharpAssemblyInfo fileName
      [ Attribute.Title (projectName)
        Attribute.Product project
        Attribute.Description summary
        Attribute.Version release.AssemblyVersion
        Attribute.FileVersion release.AssemblyVersion
        Attribute.InformationalVersion release.NugetVersion ]

// Generate assembly info files with the right version & up-to-date information
Target "AssemblyInfo" (fun _ ->
    let fsProjs =  !! "src/**/*.fsproj" |> Seq.filter (fun s -> not <| s.Contains("preview"))
    let csProjs = !! "src/**/*.csproj" |> Seq.filter (fun s -> not <| s.Contains("preview"))
    fsProjs |> Seq.iter genFSAssemblyInfo
    csProjs |> Seq.iter genCSAssemblyInfo
)

let dotnetExePath = if isWindows then "dotnetcore/dotnet.exe" else "dotnetcore/dotnet" |> FullName

Target "InstallDotNetCore" (fun _ ->
    let correctVersionInstalled = 
        try
            if FileInfo(Path.Combine(dotnetCliPath.FullName,"dotnet.exe")).Exists then
                let processResult = 
                    ExecProcessAndReturnMessages (fun info ->  
                    info.FileName <- dotnetExePath
                    info.WorkingDirectory <- Environment.CurrentDirectory
                    info.Arguments <- "--version") (TimeSpan.FromMinutes 30.)

                processResult.Messages |> separated "" = dotnetcliVersion
                
            else
                false
        with 
        | _ -> false

    if correctVersionInstalled then
        tracefn "dotnetcli %s already installed" dotnetcliVersion
    else
        CleanDir dotnetCliPath.FullName
        let zipFileName = sprintf "dotnet-dev-win-x64.%s.zip" dotnetcliVersion
        let downloadPath = sprintf "https://dotnetcli.blob.core.windows.net/dotnet/Sdk/%s/%s" dotnetcliVersion zipFileName
        let localPath = Path.Combine(dotnetCliPath.FullName, zipFileName)

        tracefn "Installing '%s' to '%s" downloadPath localPath
        
        use webclient = new Net.WebClient()
        webclient.DownloadFile(downloadPath, localPath)

        System.IO.Compression.ZipFile.ExtractToDirectory(localPath, dotnetCliPath.FullName)

    let oldPath = System.Environment.GetEnvironmentVariable("PATH")
    System.Environment.SetEnvironmentVariable("PATH", sprintf "%s%s%s" dotnetCliPath.FullName (System.IO.Path.PathSeparator.ToString()) oldPath)
)

// --------------------------------------------------------------------------------------
// Clean build results

Target "Clean" (fun _ ->
    CleanDirs [buildDir; tempDir]
)

Target "CleanDocs" (fun _ ->
    CleanDirs ["docs/output"]
)

// --------------------------------------------------------------------------------------
// Build library & test project

Target "Build" (fun _ ->
    !! solutionFile
    |> MSBuildRelease "" "Rebuild"
    |> ignore
)


Target "DotnetRestore" (fun _ ->
    netcoreFiles
    |> Seq.iter (fun proj ->
        DotNetCli.Restore (fun c ->
            { c with
                Project = proj
                ToolPath = dotnetExePath 
            }) 
    )
)

Target "DotnetBuild" (fun _ ->
    netcoreFiles
    |> Seq.iter (fun proj ->
        DotNetCli.Build (fun c ->
            { c with
                Project = proj
                ToolPath = dotnetExePath
            })
    )
)

Target "DotnetPackage" (fun _ ->
    // netcoreFiles
    // |> Seq.iter (fun proj ->
    //     DotNetCli.Pack (fun c ->
    //         { c with
    //             Project = proj
    //             ToolPath = dotnetExePath
    //             AdditionalArgs = ["/p:RuntimeIdentifier=win7-x64"]
    //         })
    // )

    ()
)


// --------------------------------------------------------------------------------------
// Build PowerShell project

Target "BuildPowerShell" (fun _ ->
    if File.Exists "src/Paket.PowerShell/System.Management.Automation.dll" = false then
        let result =
            ExecProcess (fun info ->
                info.FileName <- Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe")
                info.Arguments <- "-executionpolicy bypass -noprofile -file src/Paket.PowerShell/System.Management.Automation.ps1") System.TimeSpan.MaxValue
        if result <> 0 then failwithf "Error copying System.Management.Automation.dll"

    !! solutionFilePowerShell
    |> MSBuildRelease "" "Rebuild"
    |> ignore
)

// --------------------------------------------------------------------------------------
// Run the unit tests using test runner

Target "RunTests" (fun _ ->
    !! testAssemblies
    |> NUnit3 (fun p ->
        { p with
            ShadowCopy = false
            WorkingDir = "tests/Paket.Tests"
            TimeOut = TimeSpan.FromMinutes 20. })
)

Target "QuickTest" (fun _ ->

    !! "src\Paket.Core\Paket.Core.fsproj"
    |> MSBuildRelease "" "Rebuild"
    |> ignore

    !! testAssemblies
    |> NUnit3 (fun p ->
        { p with
            ShadowCopy = false
            WorkingDir = "tests/Paket.Tests"
            TimeOut = TimeSpan.FromMinutes 20. })
)


Target "RunIntegrationTests" (fun _ ->
    !! integrationTestAssemblies
    |> NUnit3 (fun p ->
        { p with
            ShadowCopy = false
            WorkingDir = "tests/Paket.Tests"
            TimeOut = TimeSpan.FromMinutes 40. })
)


// --------------------------------------------------------------------------------------
// Build a NuGet package

let mergeLibs = ["paket.exe"; "Paket.Core.dll"; "FSharp.Core.dll"; "Newtonsoft.Json.dll"; "Argu.dll"; "Chessie.dll"; "Mono.Cecil.dll"]

Target "MergePaketTool" (fun _ ->
    CreateDir buildMergedDir

    let toPack =
        mergeLibs
        |> List.map (fun l -> buildDir @@ l)
        |> separated " "

    let result =
        ExecProcess (fun info ->
            info.FileName <- currentDirectory </> "packages" </> "build" </> "ILRepack" </> "tools" </> "ILRepack.exe"
            info.Arguments <- sprintf "/verbose /lib:%s /ver:%s /out:%s %s" buildDir release.AssemblyVersion (buildMergedDir @@ "paket.exe") toPack
            ) (TimeSpan.FromMinutes 5.)

    if result <> 0 then failwithf "Error during ILRepack execution."
)

Target "MergePowerShell" (fun _ ->
    CreateDir buildMergedDirPS

    let toPack =
        mergeLibs @ ["Paket.PowerShell.dll"]
        |> List.map (fun l -> buildDir @@ l)
        |> separated " "

    let result =
        ExecProcess (fun info ->
            info.FileName <- currentDirectory </> "packages" </> "build" </> "ILRepack" </> "tools" </> "ILRepack.exe"
            info.Arguments <- sprintf "/verbose /lib:%s /out:%s %s" buildDir (buildMergedDirPS @@ "Paket.PowerShell.dll") toPack
            ) (TimeSpan.FromMinutes 5.)

    if result <> 0 then failwithf "Error during ILRepack execution."

    // copy psd1 & set version
    CopyFile (buildMergedDirPS @@ "ArgumentTabCompletion.ps1") "src/Paket.PowerShell/ArgumentTabCompletion.ps1"
    let psd1 = buildMergedDirPS @@ "Paket.PowerShell.psd1"
    CopyFile psd1 "src/Paket.PowerShell/Paket.PowerShell.psd1"
    use psd = File.AppendText psd1
    psd.WriteLine ""
    psd.WriteLine (sprintf "ModuleVersion = '%s'" release.AssemblyVersion)
    psd.WriteLine "}"
)

Target "SignAssemblies" (fun _ ->
    let pfx = "code-sign.pfx"
    if not <| fileExists pfx then
        traceImportant (sprintf "%s not found, skipped signing assemblies" pfx)
    else

    let filesToSign = 
        !! "bin/**/*.exe"
        ++ "bin/**/Paket.Core.dll"

    filesToSign
        |> Seq.iter (fun executable ->
            let signtool = currentDirectory @@ "tools" @@ "SignTool" @@ "signtool.exe"
            let args = sprintf "sign /f %s /t http://timestamp.comodoca.com/authenticode %s" pfx executable
            let result =
                ExecProcess (fun info ->
                    info.FileName <- signtool
                    info.Arguments <- args) System.TimeSpan.MaxValue
            if result <> 0 then failwithf "Error during signing %s with %s" executable pfx)
)

Target "NuGet" (fun _ ->    
    !! "integrationtests/**/paket.template" |> Seq.iter DeleteFile
    
    let files = !! "src/**/*.preview*" |> Seq.toList
    for file in files do
        File.Move(file,file + ".temp")

    Paket.Pack (fun p -> 
        { p with 
            ToolPath = "bin/merged/paket.exe" 
            Version = release.NugetVersion
            ReleaseNotes = toLines release.Notes })

    for file in files do
        File.Move(file + ".temp",file)
)

Target "MergeDotnetCoreIntoNuget" (fun _ ->

//    let nupkg = sprintf "./temp/Paket.Core.%s.nupkg" (release.NugetVersion) |> Path.GetFullPath
//    let netcoreNupkg = sprintf "./temp/dotnetcore/Paket.Core.%s.nupkg" (release.NugetVersion) |> Path.GetFullPath
//
//    Shell.Exec(
//      dotnetExePath, 
//      sprintf """mergenupkg --source "%s" --other "%s" --framework netstandard1.6 """ nupkg netcoreNupkg,
//      "src/Paket.Core/Paket.Core/")
//    |> fun exitCode -> if exitCode <> 0 then failwithf "mergenupkg exited with exit code %d" exitCode

    ()
)

Target "PublishChocolatey" (fun _ ->
    let chocoDir = tempDir </> "Choco"
    let files = !! (tempDir </> "*PowerShell*")
    if isMono then
        files
        |> Seq.iter File.Delete
    else
        CleanDir chocoDir
        files
        |> CopyTo chocoDir

        Paket.Push (fun p -> 
            { p with 
                ToolPath = "bin/merged/paket.exe"
                PublishUrl = "https://chocolatey.org/"
                ApiKey = getBuildParam "ChocoKey"
                WorkingDir = chocoDir })

        CleanDir chocoDir
)

Target "PublishNuGet" (fun _ ->
    if hasBuildParam "PublishBootstrapper" |> not then
        !! (tempDir </> "*bootstrapper*")
        |> Seq.iter File.Delete

    !! (tempDir </> "dotnetcore" </> "*.nupkg")
    |> Seq.iter File.Delete

    Paket.Push (fun p -> 
        { p with 
            ToolPath = "bin/merged/paket.exe"
            WorkingDir = tempDir }) 
)


// --------------------------------------------------------------------------------------
// Generate the documentation

Target "GenerateReferenceDocs" (fun _ ->
    if not <| executeFSIWithArgs "docs/tools" "generate.fsx" ["--define:RELEASE"; "--define:REFERENCE"] [] then
      failwith "generating reference documentation failed"
)

let generateHelp' commands fail debug =
    let args =
        [ if not debug then yield "--define:RELEASE"
          if commands then yield "--define:COMMANDS"
          yield "--define:HELP"]

    if executeFSIWithArgs "docs/tools" "generate.fsx" args [] then
        traceImportant "Help generated"
    else
        if fail then
            failwith "generating help documentation failed"
        else
            traceImportant "generating help documentation failed"

    CleanDir "docs/output/commands"

let generateHelp commands fail =
    generateHelp' commands fail false

Target "GenerateHelp" (fun _ ->
    DeleteFile "docs/content/release-notes.md"
    CopyFile "docs/content/" "RELEASE_NOTES.md"
    Rename "docs/content/release-notes.md" "docs/content/RELEASE_NOTES.md"

    DeleteFile "docs/content/license.md"
    CopyFile "docs/content/" "LICENSE.txt"
    Rename "docs/content/license.md" "docs/content/LICENSE.txt"

    CopyFile buildDir "packages/FSharp.Core/lib/net40/FSharp.Core.sigdata"
    CopyFile buildDir "packages/FSharp.Core/lib/net40/FSharp.Core.optdata"

    generateHelp true true
)

Target "GenerateHelpDebug" (fun _ ->
    DeleteFile "docs/content/release-notes.md"
    CopyFile "docs/content/" "RELEASE_NOTES.md"
    Rename "docs/content/release-notes.md" "docs/content/RELEASE_NOTES.md"

    DeleteFile "docs/content/license.md"
    CopyFile "docs/content/" "LICENSE.txt"
    Rename "docs/content/license.md" "docs/content/LICENSE.txt"

    generateHelp' true true true
)

Target "KeepRunning" (fun _ ->    
    use watcher = !! "docs/content/**/*.*" |> WatchChanges (fun changes ->
         generateHelp false false
    )

    traceImportant "Waiting for help edits. Press any key to stop."

    System.Console.ReadKey() |> ignore

    watcher.Dispose()
)

Target "GenerateDocs" DoNothing

// --------------------------------------------------------------------------------------
// Release Scripts

Target "ReleaseDocs" (fun _ ->
    let tempDocsDir = "temp/gh-pages"
    CleanDir tempDocsDir
    Repository.cloneSingleBranch "" (gitHome + "/" + gitName + ".git") "gh-pages" tempDocsDir

    Git.CommandHelper.runSimpleGitCommand tempDocsDir "rm . -f -r" |> ignore
    CopyRecursive "docs/output" tempDocsDir true |> tracefn "%A"    
    
    File.WriteAllText("temp/gh-pages/latest",sprintf "https://github.com/fsprojects/Paket/releases/download/%s/paket.exe" release.NugetVersion)
    File.WriteAllText("temp/gh-pages/stable",sprintf "https://github.com/fsprojects/Paket/releases/download/%s/paket.exe" stable.NugetVersion)

    StageAll tempDocsDir
    Git.Commit.Commit tempDocsDir (sprintf "Update generated documentation for version %s" release.NugetVersion)
    Branches.push tempDocsDir
)

#load "paket-files/build/fsharp/FAKE/modules/Octokit/Octokit.fsx"
open Octokit

Target "ReleaseGitHub" (fun _ ->
    let user =
        match getBuildParam "github-user" with
        | s when not (String.IsNullOrWhiteSpace s) -> s
        | _ -> getUserInput "Username: "
    let pw =
        match getBuildParam "github-pw" with
        | s when not (String.IsNullOrWhiteSpace s) -> s
        | _ -> getUserPassword "Password: "
    let remote =
        Git.CommandHelper.getGitResult "" "remote -v"
        |> Seq.filter (fun (s: string) -> s.EndsWith("(push)"))
        |> Seq.tryFind (fun (s: string) -> s.Contains(gitOwner + "/" + gitName))
        |> function None -> gitHome + "/" + gitName | Some (s: string) -> s.Split().[0]

    StageAll ""
    Git.Commit.Commit "" (sprintf "Bump version to %s" release.NugetVersion)
    Branches.pushBranch "" remote (Information.getBranchName "")

    Branches.tag "" release.NugetVersion
    Branches.pushTag "" remote release.NugetVersion
    
    // release on github
    createClient user pw
    |> createDraft gitOwner gitName release.NugetVersion (release.SemVer.PreRelease <> None) release.Notes 
    |> uploadFile "./bin/merged/paket.exe"
    |> uploadFile "./bin/paket.bootstrapper.exe"
    |> uploadFile ".paket/paket.targets"
    |> releaseDraft
    |> Async.RunSynchronously
)

Target "Release" DoNothing
Target "BuildPackage" DoNothing

// --------------------------------------------------------------------------------------
// Run all targets by default. Invoke 'build <Target>' to override

Target "All" DoNothing

"Clean"
  ==> "AssemblyInfo"
  =?> ("InstallDotNetCore", not <| hasBuildParam "DISABLE_NETCORE")
  =?> ("DotnetRestore", not <| hasBuildParam "DISABLE_NETCORE")
  =?> ("DotnetBuild", not <| hasBuildParam "DISABLE_NETCORE")
  ==> "Build"
  =?> ("DotnetPackage", not <| hasBuildParam "DISABLE_NETCORE")
  =?> ("BuildPowerShell", not isMono)
  ==> "RunTests"
  =?> ("GenerateReferenceDocs",isLocalBuild && not isMono)
  =?> ("GenerateDocs",isLocalBuild && not isMono)
  ==> "All"
  =?> ("ReleaseDocs",isLocalBuild && not isMono)

"All"
  =?> ("RunIntegrationTests", not <| hasBuildParam "SkipIntegrationTests")
  ==> "MergePaketTool"
  =?> ("MergePowerShell", not isMono)
  ==> "SignAssemblies"
  ==> "NuGet"
  =?> ("MergeDotnetCoreIntoNuget", not <| hasBuildParam "DISABLE_NETCORE")
  ==> "BuildPackage"

"CleanDocs"
  ==> "GenerateHelp"
  ==> "GenerateReferenceDocs"
  ==> "GenerateDocs"

"CleanDocs"
  ==> "GenerateHelpDebug"

"GenerateHelp"
  ==> "KeepRunning"

"BuildPackage"
  ==> "PublishChocolatey"
  ==> "PublishNuGet"

"PublishNuGet"
  ==> "ReleaseGitHub"
  ==> "Release"

"ReleaseGitHub"
  ?=> "ReleaseDocs"

"ReleaseDocs"
  ==> "Release"

RunTargetOrDefault "All"