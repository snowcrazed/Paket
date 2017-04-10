﻿module Paket.Queries // mostly logic which was in PublicAPI.fs but is useful to other places (like script generation)

open Domain
open System.IO
open System

type QualifiedPackageName = QualifiedPackageName of GroupName * PackageName
    with
        static member FromStrings (groupName: string option, packageName: string) =
            let groupName = 
                match groupName with
                | None -> Constants.MainDependencyGroup
                | Some name -> GroupName name
            let packageName = PackageName packageName
            QualifiedPackageName(groupName, packageName)

type PaketFiles = 
| JustDependencies    of DependenciesFile
| DependenciesAndLock of DependenciesFile * LockFile
    with
        static member LocateFromDirectory (directory: DirectoryInfo) =
            let rec findInPath (dir:DirectoryInfo , withError) =
                        let path = Path.Combine(dir.FullName,Constants.DependenciesFileName)
                        if File.Exists(path) then
                            path
                        else
                            let parent = dir.Parent
                            match parent with
                            | null ->
                                if withError then
                                    failwithf "Could not find '%s'. To use Paket with this solution, please run 'paket init' first.%sIf you have already run 'paket.init' then ensure that '%s' is located in the top level directory of your repository.%sLike this:%sMySourceDir%s  .paket%s  paket.dependencies" 
                                      Constants.DependenciesFileName Environment.NewLine Constants.DependenciesFileName Environment.NewLine Environment.NewLine Environment.NewLine Environment.NewLine
                                else
                                    Constants.DependenciesFileName
                            | _ -> findInPath(parent, withError)

            let dependenciesFile = 
                findInPath(directory,true)
                |> DependenciesFile.ReadFromFile
            
            
            let file = dependenciesFile.FindLockfile()
            if file.Exists then
                let lockFile = file.FullName |> LockFile.LoadFrom
                PaketFiles.DependenciesAndLock(dependenciesFile, lockFile)
            else
                PaketFiles.JustDependencies dependenciesFile

let getLockFileFromDependenciesFile dependenciesFileName =
    let lockFileName = DependenciesFile.FindLockfile dependenciesFileName
    LockFile.LoadFrom lockFileName.FullName

let listPackages (packages: System.Collections.Generic.KeyValuePair<GroupName*PackageName, PackageResolver.ResolvedPackage> seq) =
    packages
    |> Seq.map (fun kv ->
            let groupName,packageName = kv.Key
            groupName.ToString(),packageName.ToString(),kv.Value.Version.ToString())
    |> Seq.toList

let getAllInstalledPackagesFromLockFile (lockFile: LockFile) =
    lockFile.GetGroupedResolution() |> listPackages

let getInstalledPackageModel (lockFile: LockFile) (QualifiedPackageName(groupName, packageName)) =
    match lockFile.Groups |> Map.tryFind groupName with
    | None -> failwithf "Group %O can't be found in paket.lock." groupName
    | Some group ->
        match group.Resolution.TryFind(packageName) with
        | None -> failwithf "Package %O is not installed in group %O." packageName groupName
        | Some resolvedPackage ->
            let packageName = resolvedPackage.Name
            let groupFolder = if groupName = Constants.MainDependencyGroup then "" else "/" + groupName.ToString()
            let folder = DirectoryInfo(sprintf "%s/packages%s/%O" lockFile.RootPath groupFolder packageName)
            let nuspec = FileInfo(sprintf "%s/packages%s/%O/%O.nuspec" lockFile.RootPath groupFolder packageName packageName)
            let nuspec = Nuspec.Load nuspec.FullName
            let files = NuGetV2.GetLibFiles(folder.FullName)
            let files = files |> Array.map (fun fi -> fi.FullName)
            InstallModel.CreateFromLibs(packageName, resolvedPackage.Version, [], files, [], [], nuspec)

let resolveFrameworkForScriptGeneration (dependencies: DependenciesFile) = lazy (
    dependencies.Groups
        |> Seq.map (fun f -> f.Value.Options.Settings.FrameworkRestrictions)
        |> Seq.map(fun restrictions ->
            match restrictions with
            | Paket.Requirements.AutoDetectFramework -> failwithf "couldn't detect framework"
            | Paket.Requirements.FrameworkRestrictionList list ->
              list |> Seq.collect (
                function
                | Paket.Requirements.FrameworkRestriction.Exactly framework
                | Paket.Requirements.FrameworkRestriction.AtLeast framework -> Seq.singleton framework
                | Paket.Requirements.FrameworkRestriction.Between (bottom,top) -> [bottom; top] |> Seq.ofList //TODO: do we need to cap the list of generated frameworks based on this? also see todo in Requirements.fs for potential generation of range for 'between'
                | Paket.Requirements.FrameworkRestriction.Portable portable -> failwithf "unhandled portable framework %s" portable
              )
          )
        |> Seq.concat
    )

let resolveEnvironmentFrameworkForScriptGeneration = lazy (
    // HACK: resolve .net version based on environment
    // list of match is incomplete / inaccurate
#if DOTNETCORE
    // Environment.Version is not supported
    //dunno what is used for, using a default
    DotNetFramework (FrameworkVersion.V4_5)
#else
    let version = Environment.Version
    match version.Major, version.Minor, version.Build, version.Revision with
    | 4, 0, 30319, 42000 -> DotNetFramework (FrameworkVersion.V4_6)
    | 4, 0, 30319, _ -> DotNetFramework (FrameworkVersion.V4_5)
    | _ -> DotNetFramework (FrameworkVersion.V4_5) // paket.exe is compiled for framework 4.5
#endif
    )