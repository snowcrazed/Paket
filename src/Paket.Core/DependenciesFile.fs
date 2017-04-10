namespace Paket
          
/// [omit]
module DependenciesFileSerializer = 
    open Requirements
    open System

    let formatVersionRange strategy (versionRequirement : VersionRequirement) : string =
        let prefix = 
            match strategy with
            | Some ResolverStrategy.Min -> "!"
            | Some ResolverStrategy.Max -> "@"
            | None -> ""

        let preReleases = 
            match versionRequirement.PreReleases with
            | No -> ""
            | PreReleaseStatus.All -> "prerelease"
            | Concrete list -> String.Join(" ",list)
            
        let version = 
            match versionRequirement.Range with
            | Minimum x when strategy = None && x = SemVer.Zero -> ""
            | Minimum x -> ">= " + x.ToString()
            | GreaterThan x -> "> " + x.ToString()
            | Specific x when strategy = None -> x.ToString()
            | Specific x -> "= " + x.ToString()
            | VersionRange.Range(_, from, _, _) 
                    when DependenciesFileParser.parseVersionRequirement ("~> " + from.ToString() + preReleases) = versionRequirement -> 
                        "~> " + from.ToString()
            | _ -> versionRequirement.ToString()
            
        let text = prefix + version
        if text <> "" && preReleases <> "" then text + " " + preReleases else text + preReleases

    let sourceString source = "source " + source

    let packageString packageName versionRequirement resolverStrategy (settings:InstallSettings) =
        let version = formatVersionRange resolverStrategy versionRequirement
        let s = settings.ToString()

        sprintf "nuget %O%s%s" packageName (if version <> "" then " " + version else "") (if s <> "" then " " + s else s)

open Domain
open System
open Requirements
open ModuleResolver
open System.IO
open Logging
open PackageResolver

/// Allows to parse and analyze paket.dependencies files.
type DependenciesFile(fileName,groups:Map<GroupName,DependenciesGroup>, textRepresentation:string []) =
    let tryMatchPackageLine packageNamePredicate (line : string) =
        let tokens = line.Split([|' '|], StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun s -> s.ToLowerInvariant().Trim())
        match List.ofArray tokens with
        | "nuget"::packageName::_ when packageNamePredicate packageName -> Some packageName
        | _ -> None

    let isPackageLine name line = tryMatchPackageLine ((=) name) line |> Option.isSome

    let findGroupBorders groupName = 
        let _,_,firstLine,lastLine =
            textRepresentation
            |> Array.fold (fun (i,currentGroup,firstLine,lastLine) line -> 
                    if line.TrimStart().StartsWith "group " then
                        let group = line.Replace("group","").Trim()
                        if currentGroup = groupName then
                            i+1,GroupName group,firstLine,(i - 1)
                        else
                            if GroupName group = groupName then
                                i+1,GroupName group,(i + 1),lastLine
                            else
                                i+1,GroupName group,firstLine,lastLine
                    else
                        i+1,currentGroup,firstLine,lastLine)
                (0,Constants.MainDependencyGroup,0,textRepresentation.Length)
        firstLine,lastLine

    let tryFindPackageLine groupName (packageName:PackageName) =
        let name = packageName.GetCompareString()
        let _,_,found =
            textRepresentation
            |> Array.fold (fun (i,currentGroup,found) line -> 
                    match found with
                    | Some _ -> i+1,currentGroup,found
                    | None ->
                        if currentGroup = groupName && isPackageLine name line then
                            i+1,currentGroup,Some i
                        else
                            if line.StartsWith "group " then
                                let group = line.Replace("group","").Trim()
                                i+1,GroupName group,found
                            else
                                i+1,currentGroup,found)
                (0,Constants.MainDependencyGroup,None)
        found

    /// Returns all direct NuGet dependencies in the given group.
    member __.GetDependenciesInGroup(groupName:GroupName) =
        match groups |> Map.tryFind groupName with
        | None -> failwithf "Group %O doesn't exist in the paket.dependencies file." groupName
        | Some group ->
            group.Packages 
            |> Seq.map (fun p -> p.Name, p.VersionRequirement)
            |> Map.ofSeq

    member this.CheckIfPackageExistsInAnyGroup (packageName:PackageName) =
        match groups |> Seq.tryFind (fun g -> g.Value.Packages |> List.exists (fun p -> p.Name = packageName)) with
        | Some group -> sprintf "%sHowever, %O was found in group %O." Environment.NewLine PackageName group.Value.Name
        | None -> ""

    member __.Groups = groups

    member this.SimplifyFrameworkRestrictions() = 
        let transform (dependenciesFile:DependenciesFile) (group:DependenciesGroup) =
            let getRestrictionList =
                let projectFrameworks = lazy ( 
                    let lockFile = dependenciesFile.FindLockfile()
                    let dir = (lockFile : FileInfo).DirectoryName
                    let projects = ProjectFile.FindAllProjects dir
                    let frameworks = projects |> Array.choose ProjectFile.getTargetFramework |> Array.distinct
                    frameworks |> Array.map FrameworkRestriction.Exactly |> List.ofArray
                )
                fun restrictions -> 
                    match restrictions with 
                    | FrameworkRestrictionList l -> l
                    | AutoDetectFramework -> projectFrameworks.Force()

            if group.Options.Settings.FrameworkRestrictions |> getRestrictionList <> [] then dependenciesFile else
            match group.Packages with
            | [] -> dependenciesFile
            | package::rest ->
                let commonRestrictions =
                    package.Settings.FrameworkRestrictions
                    |> getRestrictionList
                    |> List.filter (fun r -> rest |> Seq.forall (fun p' -> p'.Settings.FrameworkRestrictions |> getRestrictionList |> List.contains r))

                match commonRestrictions with
                | [] -> dependenciesFile
                | _ ->
                    let newDependenciesFile = dependenciesFile.AddFrameworkRestriction(group.Name,commonRestrictions)
                    group.Packages
                     |> List.fold (fun (d:DependenciesFile) package ->
                            let oldRestrictions = package.Settings.FrameworkRestrictions |> getRestrictionList
                            let newRestrictions = oldRestrictions |> List.filter (fun r -> commonRestrictions |> List.contains r |> not)
                            if oldRestrictions = newRestrictions then d else
                            let (d:DependenciesFile) = d.Remove(group.Name,package.Name)
                            let installSettings = { package.Settings with FrameworkRestrictions = FrameworkRestrictionList newRestrictions }
                            let vr = { VersionRequirement = package.VersionRequirement; ResolverStrategy = package.ResolverStrategyForDirectDependencies }
                            d.AddAdditionalPackage(group.Name, package.Name,vr,installSettings)) newDependenciesFile

        this.Groups
        |> Seq.map (fun kv -> kv.Value)
        |> Seq.fold transform this

    member this.GetGroup groupName =
        match this.Groups |> Map.tryFind groupName with
        | Some g -> g
        | None -> failwithf "Group %O was not found in %s." groupName fileName

    member __.HasPackage (groupName, name : PackageName) = 
        match groups |> Map.tryFind groupName with
        | None -> false
        | Some g -> g.Packages |> List.exists (fun p -> p.Name = name)

    member __.GetPackage (groupName, name : PackageName) = groups.[groupName].Packages |> List.find (fun p -> p.Name = name)
    member __.FileName = fileName
    member __.Lines = textRepresentation

    member this.Resolve(force, getSha1, getVersionF, getPackageDetailsF, groupsToResolve:Map<GroupName,_>, updateMode) =
        let resolveGroup groupName _ =
            let group = this.GetGroup groupName

            let resolveSourceFile (file:ResolvedSourceFile) : (PackageRequirement list * UnresolvedSource list) =
                let remoteDependenciesFile =
                    RemoteDownload.downloadDependenciesFile(force,Path.GetDirectoryName fileName, groupName, DependenciesFile.FromCode, file)
                    |> Async.RunSynchronously

                // We do not support groups in reference files yet
                let mainGroup = remoteDependenciesFile.Groups.[Constants.MainDependencyGroup]
                mainGroup.Packages,mainGroup.RemoteFiles

            let remoteFiles = ModuleResolver.Resolve(resolveSourceFile,getSha1,group.RemoteFiles)
        
            let remoteDependencies = 
                remoteFiles
                |> List.map (fun f -> f.Dependencies)
                |> List.fold Set.union Set.empty
                |> Seq.map (fun (n, v) -> 
                        { Name = n
                          VersionRequirement = v
                          ResolverStrategyForDirectDependencies = Some ResolverStrategy.Max
                          ResolverStrategyForTransitives = Some ResolverStrategy.Max
                          Parent = PackageRequirementSource.DependenciesFile fileName
                          Graph = []
                          Sources = group.Sources
                          Settings = group.Options.Settings })
                |> Seq.toList
            
            if String.IsNullOrWhiteSpace fileName |> not then
                RemoteDownload.DownloadSourceFiles(Path.GetDirectoryName fileName, groupName, force, remoteFiles)

            let resolution =
                PackageResolver.Resolve(
                    getVersionF, 
                    getPackageDetailsF, 
                    groupName,
                    group.Options.ResolverStrategyForDirectDependencies,
                    group.Options.ResolverStrategyForTransitives,
                    group.Options.Settings.FrameworkRestrictions,
                    remoteDependencies @ group.Packages |> Set.ofList,
                    updateMode)

            { ResolvedPackages = resolution
              ResolvedSourceFiles = remoteFiles }

        groupsToResolve
        |> Map.map resolveGroup 


    member private this.AddFrameworkRestriction(groupName, frameworkRestrictions:FrameworkRestriction list) =
        if List.isEmpty frameworkRestrictions then this else
        let restrictionString = sprintf "framework %s" (String.Join(", ",frameworkRestrictions))

        let list = new System.Collections.Generic.List<_>()
        list.AddRange textRepresentation

        match groups |> Map.tryFind groupName with 
        | None -> list.Add(restrictionString)
        | Some group ->
            let firstGroupLine,_ = findGroupBorders groupName
            let pos = ref firstGroupLine
            while list.Count > !pos && list.[!pos].TrimStart().StartsWith "source" do
                pos := !pos + 1

            list.Insert(!pos,restrictionString)
       
        DependenciesFile(
            list 
            |> Seq.toArray
            |> DependenciesFileParser.parseDependenciesFile fileName false)

    member __.AddAdditionalPackage(groupName, packageName:PackageName,versionRequirement,resolverStrategy,settings,?pinDown) =
        let pinDown = defaultArg pinDown false
        let packageString = DependenciesFileSerializer.packageString packageName versionRequirement resolverStrategy settings

        // Try to find alphabetical matching position to insert the package
        let isPackageInLastSource (p:PackageRequirement) =
            match groups |> Map.tryFind groupName with
            | None -> true
            | Some group ->
                match group.Sources with
                | [] -> true
                | sources -> 
                    let lastSource = Seq.last sources
                    group.Sources |> Seq.exists (fun s -> s = lastSource)

        let smaller = 
            match groups |> Map.tryFind groupName with
            | None -> []
            | Some group ->
                group.Packages 
                |> Seq.takeWhile (fun (p:PackageRequirement) -> p.Name <= packageName || not (isPackageInLastSource p)) 
                |> List.ofSeq
        
        let list = new System.Collections.Generic.List<_>()
        list.AddRange textRepresentation
        let newGroupInserted =
            match groups |> Map.tryFind groupName with
            | None -> 
                if list.Count > 0 then
                    list.Add("")
                list.Add(sprintf "group %O" groupName)
                list.Add(DependenciesFileSerializer.sourceString Constants.DefaultNuGetStream)
                list.Add("")
                true
            | _ -> false

        match tryFindPackageLine groupName packageName with
        | Some pos -> 
            let package = DependenciesFileParser.parsePackageLine(groups.[groupName].Sources,PackageRequirementSource.DependenciesFile fileName,list.[pos])

            if versionRequirement.Range.IsIncludedIn(package.VersionRequirement.Range) then
                list.[pos] <- packageString
            else
                list.Insert(pos + 1, packageString)
        | None -> 
            let firstGroupLine,lastGroupLine = findGroupBorders groupName
            if pinDown then
                if newGroupInserted then
                    list.Add(packageString)
                else
                    list.Insert(lastGroupLine, packageString)
            else
                match smaller with
                | [] -> 
                    match groups |> Map.tryFind groupName with 
                    | None -> list.Add(packageString)
                    | Some group ->
                        match group.Packages with
                        | [] ->
                            if group.RemoteFiles <> [] then
                                list.Insert(firstGroupLine,"")
                    
                            match group.Sources with
                            | [] -> 
                                list.Insert(firstGroupLine,packageString)
                                list.Insert(firstGroupLine,"")
                                list.Insert(firstGroupLine,DependenciesFileSerializer.sourceString Constants.DefaultNuGetStream)
                            | _ -> list.Insert(lastGroupLine, packageString)
                        | p::_ -> 
                            match tryFindPackageLine groupName p.Name with
                            | None -> list.Add packageString
                            | Some pos -> list.Insert(pos,packageString)
                | _ -> 
                    let p = Seq.last smaller

                    match tryFindPackageLine groupName p.Name with
                    | None -> list.Add packageString
                    | Some found -> 
                        let pos = ref (found + 1)
                        let skipped = ref false
                        while !pos < textRepresentation.Length - 1 &&
                                (String.IsNullOrWhiteSpace textRepresentation.[!pos] || 
                                 String.startsWithIgnoreCase "source" textRepresentation.[!pos] ||
                                 String.startsWithIgnoreCase "cache" textRepresentation.[!pos]) do
                            if (String.startsWithIgnoreCase "source" textRepresentation.[!pos]) ||
                               (String.startsWithIgnoreCase "cache" textRepresentation.[!pos])
                            then
                                skipped := true
                            pos := !pos + 1
                            
                        if !skipped then
                            list.Insert(!pos,packageString)
                        else
                            list.Insert(found + 1,packageString)
        
        DependenciesFile(
            list 
            |> Seq.toArray
            |> DependenciesFileParser.parseDependenciesFile fileName false)


    member this.AddAdditionalPackage(groupName, packageName:PackageName,version:string,settings) =
        let vr = DependenciesFileParser.parseVersionString version

        this.AddAdditionalPackage(groupName, packageName,vr,settings)

    member this.AddAdditionalPackage(groupName, packageName:PackageName,vr:VersionStrategy,settings) =
        this.AddAdditionalPackage(groupName, packageName,vr.VersionRequirement,vr.ResolverStrategy,settings)

    member this.AddFixedPackage(groupName, packageName:PackageName,version:string,settings) =
        let vr = DependenciesFileParser.parseVersionString version

        let resolverStrategy,versionRequirement = 
            match groups |> Map.tryFind groupName with
            | None -> vr.ResolverStrategy,vr.VersionRequirement
            | Some group ->
                match group.Packages |> List.tryFind (fun p -> p.Name = packageName) with
                | Some package -> 
                    package.ResolverStrategyForTransitives,
                    match package.VersionRequirement.Range with
                    | OverrideAll(_) -> package.VersionRequirement
                    | _ -> vr.VersionRequirement
                | None -> vr.ResolverStrategy,vr.VersionRequirement

        this.AddAdditionalPackage(groupName, packageName,versionRequirement,resolverStrategy,settings,true)

    member this.AddFixedPackage(groupName, packageName:PackageName,version:string) =
        this.AddFixedPackage(groupName, packageName,version,InstallSettings.Default)

    member this.RemovePackage(groupName, packageName:PackageName) =
        match tryFindPackageLine groupName packageName with 
        | None -> this
        | Some pos ->
            let removeElementAt index myArr =
                [|  for i = 0 to Array.length myArr - 1 do 
                       if i <> index then yield myArr.[ i ] |]
            
            let fileName, groups, lines = 
              removeElementAt pos textRepresentation
              |> DependenciesFileParser.parseDependenciesFile fileName false
            
            let filteredGroups, filteredLines =
              groups
              |> Seq.map(fun item -> item.Value)
              |> Seq.filter(fun group -> group.Packages.IsEmpty && group.Name <> Constants.MainDependencyGroup)
              |> Seq.fold(fun (groups, (lines:string[])) emptyGroup ->
                  groups 
                  |> Map.remove emptyGroup.Name,
                  lines 
                  |> Array.filter(fun line -> not(line.StartsWith "group " && GroupName(line.Replace("group","")) = emptyGroup.Name))
                ) (groups, lines)

            DependenciesFile(fileName, filteredGroups, filteredLines)

    member this.Add(groupName, packageName,version:string,?installSettings : InstallSettings) =
        let installSettings = defaultArg installSettings InstallSettings.Default
        if this.HasPackage(groupName, packageName) && String.IsNullOrWhiteSpace version then 
            traceWarnfn "%s contains package %O in group %O already. ==> Ignored" fileName packageName groupName
            this
        else
            if version = "" then
                tracefn "Adding %O to %s into group %O" packageName fileName groupName
            else
                tracefn "Adding %O %s to %s into group %O" packageName version fileName groupName
            this.AddAdditionalPackage(groupName, packageName,version,installSettings)

    member this.Remove(groupName, packageName) =
        if this.HasPackage(groupName, packageName) then
            tracefn "Removing %O from %s (group %O)" packageName fileName groupName
            this.RemovePackage(groupName, packageName)
        else
            traceWarnfn "%s doesn't contain package %O in group %O. ==> Ignored" fileName packageName groupName
            this

    member this.UpdatePackageVersion(groupName, packageName, version:string) = 
        if this.HasPackage(groupName,packageName) then
            let vr = DependenciesFileParser.parseVersionString version

            tracefn "Updating %O to version %s in %s group %O" packageName version fileName groupName
            let newLines = 
                this.Lines 
                |> Array.map (fun l -> 
                    let name = packageName.GetCompareString()
                    if isPackageLine name l then 
                        let p = this.GetPackage(groupName,packageName)
                        match vr.VersionRequirement.Range with
                        | Specific _ ->
                            let v = SemVer.Parse version
                            if not <| p.VersionRequirement.IsInRange v then
                                failwithf "Version %O doesn't match the version requirement %O for package %O that was specified in paket.dependencies" v p.VersionRequirement packageName
                        | _ -> ()

                        DependenciesFileSerializer.packageString packageName vr.VersionRequirement vr.ResolverStrategy p.Settings
                    else l)

            DependenciesFile(DependenciesFileParser.parseDependenciesFile this.FileName false newLines)
        else 
            traceWarnfn "%s doesn't contain package %O in group %O. ==> Ignored" fileName packageName groupName
            this

    member this.UpdateFilteredPackageVersion(groupName, packageFilter: PackageFilter, version:string) =
        let vr = DependenciesFileParser.parseVersionString version

        tracefn "Updating %O to version %s in %s group %O" packageFilter version fileName groupName
        let newLines =
            this.Lines
            |> Array.map (fun l ->
                match tryMatchPackageLine (PackageName >> packageFilter.Match) l with
                | Some matchedName ->
                    let matchedPackageName = PackageName matchedName
                    let p = this.GetPackage(groupName,matchedPackageName)
                    DependenciesFileSerializer.packageString matchedPackageName vr.VersionRequirement vr.ResolverStrategy p.Settings
                | None -> l)

        DependenciesFile(DependenciesFileParser.parseDependenciesFile this.FileName false newLines)

    member this.RootPath = FileInfo(fileName).Directory.FullName

    override __.ToString() = String.Join(Environment.NewLine, textRepresentation |> Array.skipWhile String.IsNullOrWhiteSpace)

    member this.Save() =
        File.WriteAllText(fileName, this.ToString())
        tracefn "Dependencies files saved to %s" fileName

    static member FromCode(rootPath,code:string) : DependenciesFile = 
        DependenciesFile(DependenciesFileParser.parseDependenciesFile (Path.Combine(rootPath,Constants.DependenciesFileName)) true <| code.Replace("\r\n","\n").Replace("\r","\n").Split('\n'))

    static member FromCode(code:string) : DependenciesFile = 
        DependenciesFile(DependenciesFileParser.parseDependenciesFile "" true <| code.Replace("\r\n","\n").Replace("\r","\n").Split('\n'))

    static member ReadFromFile fileName : DependenciesFile = 
        verbosefn "Parsing %s" fileName
        DependenciesFile(DependenciesFileParser.parseDependenciesFile fileName true <| File.ReadAllLines fileName)

    /// Find the matching lock file to a dependencies file
    static member FindLockfile(dependenciesFileName) =
        FileInfo(Path.Combine(FileInfo(dependenciesFileName).Directory.FullName, Constants.LockFileName))

    static member FindLocalfile(dependenciesFileName) =
        FileInfo(Path.Combine(FileInfo(dependenciesFileName).Directory.FullName, Constants.LocalFileName))

    /// Find the matching lock file to a dependencies file
    member this.FindLockfile() = DependenciesFile.FindLockfile this.FileName
