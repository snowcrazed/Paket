﻿module Paket.Domain

open System
open System.IO
open System.Text.RegularExpressions

/// Represents a NuGet package name
[<System.Diagnostics.DebuggerDisplay("{ToString()}")>]
[<CustomEquality;CustomComparison>]
type PackageName =
| PackageName of string * string

    member this.GetCompareString() =
        match this with
        | PackageName(_,id) -> id

    override this.ToString() = 
        match this with
        | PackageName(name,_) -> name

    override this.Equals(that) = 
        match that with
        | :? PackageName as that -> this.GetCompareString() = that.GetCompareString()
        | _ -> false

    override this.GetHashCode() = hash (this.GetCompareString())

    interface System.IComparable with
       member this.CompareTo that = 
          match that with 
          | :? PackageName as that -> StringComparer.Ordinal.Compare(this.GetCompareString(), that.GetCompareString())
          | _ -> invalidArg "that" "cannot compare value of different types"

/// Function to convert a string into a NuGet package name
let PackageName(name:string) = PackageName.PackageName(name.Trim(),name.ToLowerInvariant().Trim())

// Represents a filter of normalized package names
[<System.Diagnostics.DebuggerDisplay("{ToString()}")>]
type PackageFilter =
| PackageName of PackageName
| PackageFilter of string

    member this.Match (packageName : PackageName) =
        match this with
        | PackageName name -> name = packageName
        | PackageFilter f ->
            let regex =
                Regex("^" + f + "$",
                    RegexOptions.Compiled 
                    ||| RegexOptions.CultureInvariant 
                    ||| RegexOptions.IgnoreCase)

            regex.IsMatch (packageName.GetCompareString())

    static member ofName name = PackageFilter.PackageName name

    override this.ToString() =
        match this with
        | PackageName name -> name.ToString()
        | PackageFilter filter -> filter

/// Represents a normalized group name
[<System.Diagnostics.DebuggerDisplay("{Item2}")>]
[<CustomEquality;CustomComparison>]
type GroupName =
| GroupName of string * string

    member this.GetCompareString() =
        match this with
        | GroupName(_,id) -> id

    override this.ToString() = 
        match this with
        | GroupName(name,_) -> name

    override this.Equals(that) = 
        match that with
        | :? GroupName as that -> this.GetCompareString() = that.GetCompareString()
        | _ -> false

    override this.GetHashCode() = hash (this.GetCompareString())

    interface System.IComparable with
       member this.CompareTo that = 
          match that with 
          | :? GroupName as that -> StringComparer.Ordinal.Compare(this.GetCompareString(), that.GetCompareString())
          | _ -> invalidArg "that" "cannot compare value of different types"

/// Function to convert a string into a group name
let GroupName(name:string) = 
    match name.ToLowerInvariant().Trim() with
    | "lib" | "runtimes" -> invalidArg "name" (sprintf "It is not allowed to use '%s' as group name." name)
    | id -> GroupName.GroupName(name.Trim(), id)

type DomainMessage = 
    | DirectoryDoesntExist of DirectoryInfo
    | DependenciesFileNotFoundInDir of DirectoryInfo
    | DependenciesFileParseError of FileInfo * exn
    | LockFileNotFound of DirectoryInfo
    | LockFileParseError of FileInfo
    | ReferencesFileParseError of FileInfo

    | PackageSourceParseError of string
    
    | InvalidCredentialsMigrationMode of string
    | PaketEnvAlreadyExistsInDirectory of DirectoryInfo
    | NugetConfigFileParseError of FileInfo
    | NugetPackagesConfigParseError of FileInfo

    | StrictModeDetected
    | DependencyNotFoundInLockFile of PackageName
    | ReferenceNotFoundInLockFile of path:string * groupName:string * packageName:PackageName

    | DownloadError of string
    | ReleasesJsonParseError
    
    | DirectoryCreateError of string 
    | FileDeleteError of string
    | FileSaveError of string

    | ConfigFileParseError
    
    | PackagingConfigParseError of file:string * error:string

    override this.ToString() = 
        match this with
        | DirectoryDoesntExist(di) -> 
            sprintf "Directory %s does not exist." di.FullName
        | DependenciesFileNotFoundInDir(di) -> 
            sprintf "Dependencies file not found in %s." di.FullName
        | DependenciesFileParseError(fi,e) -> 
            sprintf "Unable to parse %s. (%s)" fi.FullName e.Message
        | LockFileNotFound(di) -> 
            sprintf "Lock file not found in %s. Create lock file by running paket install." di.FullName
        | LockFileParseError(fi) -> 
            sprintf "Unable to parse lock %s." fi.FullName
        | ReferencesFileParseError(fi) -> 
            sprintf "Unable to parse %s" fi.FullName
        
        | PackageSourceParseError(source) -> 
            sprintf "Unable to parse package source: %s." source

        | InvalidCredentialsMigrationMode(mode) ->
            sprintf "Invalid credentials migration mode: %s." mode
        | PaketEnvAlreadyExistsInDirectory(di) ->
            sprintf "Paket is already present in %s. Run with --force to overwrite." di.FullName
        | NugetConfigFileParseError(fi) ->
            sprintf "Unable to parse %s" fi.FullName
        | NugetPackagesConfigParseError(fi) ->
            sprintf "Unable to parse %s" fi.FullName

        | StrictModeDetected -> 
            "Strict mode detected. Command not executed."
        | DependencyNotFoundInLockFile(packageName) -> 
            sprintf "Package %O from paket.dependencies not found in lock file." packageName
        | ReferenceNotFoundInLockFile(path, groupName, packageName) -> 
            sprintf "Package %O from %s not found in lock file in group %s." packageName path groupName

        | DownloadError url ->
            sprintf "Error occured while downloading from %s." url
        | ReleasesJsonParseError ->
            "Unable to parse Json from GitHub releases API."
        
        | DirectoryCreateError path ->
            sprintf "Unable to create directory %s." path
        | FileDeleteError path ->
            sprintf "Unable to delete file %s." path
        | FileSaveError path ->
            sprintf "Unable to save file %s." path

        | ConfigFileParseError -> "Unable to parse Paket configuration from paket.config."

        | PackagingConfigParseError(file,error) ->
            sprintf "Unable to parse template file %s: %s." file error
