﻿module Paket.Git.Handling
open Paket.Utils
open System
open System.IO
open Paket.Logging
open Paket

type GitLinkOrigin =
| RemoteGitOrigin of string
| LocalGitOrigin  of string

let extractUrlParts (gitConfig:string) =
    let isOperator operator = VersionRange.BasicOperators |> List.exists ((=) operator)
    let url,commit,options = 
        match gitConfig.Split([|' '|],StringSplitOptions.RemoveEmptyEntries) |> List.ofArray with
        | part::_ ->
            let rest = gitConfig.Substring(part.Length)
            let parts = 
                [ let current = Text.StringBuilder()
                  let quoted = ref false
                  for x in rest do
                    if x = '"' then
                        quoted := not !quoted

                    if (x = ':' || x = ' ') && not !quoted then
                        yield current.ToString()
                        if x = ':' then
                            yield ":"
                        current.Clear() |> ignore
                    else
                        current.Append x |> ignore
                
                  yield current.ToString()]
                |> List.filter (String.IsNullOrWhiteSpace >> not)

            match parts with
            | k::colon::_ when colon = ":" && (k.ToLower() = "build" || k.ToLower() = "os" || k.ToLower() = "packages")  -> 
                part,None,rest
            | operator::version::operator2::version2::prerelease::options when 
                    isOperator operator && isOperator operator2 && 
                      not (prerelease.ToLower() = "build" || prerelease.ToLower() = "os" || prerelease.ToLower() = "packages" || prerelease = ":") 
               -> 
                let startPos = gitConfig.Substring(part.Length).IndexOf(prerelease)
                let options = gitConfig.Substring(part.Length + startPos + prerelease.Length)
                part,Some(operator + " " + version + " " + operator2 + " " + version2 +  " " + prerelease),options
            | operator::version::prerelease::options when 
                    isOperator operator && not (isOperator prerelease) && 
                      not (prerelease.ToLower() = "build" || prerelease.ToLower() = "os" || prerelease.ToLower() = "packages" || prerelease = ":") 
               -> 
                let startPos = gitConfig.Substring(part.Length).IndexOf(prerelease)
                let options = gitConfig.Substring(part.Length + startPos + prerelease.Length)
                part,Some(operator + " " + version +  " " + prerelease),options
            | operator::version::operator2::version2::options when isOperator operator && isOperator operator2 -> 
                let startPos = gitConfig.Substring(part.Length).IndexOf(version2)
                let options = gitConfig.Substring(part.Length + startPos + version2.Length)
                part,Some(operator + " " + version + " " + operator2 + " " + version2),options
            | operator::version::options when isOperator operator -> 
                let startPos = gitConfig.Substring(part.Length).IndexOf(version)
                let options = gitConfig.Substring(part.Length + startPos + version.Length)
                part,Some(operator + " " + version),options
            | commit::options -> 
                let startPos = gitConfig.Substring(part.Length).IndexOf(commit)
                let options = gitConfig.Substring(part.Length + startPos + commit.Length)
                part,Some commit,options
            | _ -> gitConfig,None,""
        | _ -> gitConfig,None,""
    
    let kvPairs = parseKeyValuePairs options

    let buildCommand = 
        match kvPairs.TryGetValue "build" with
        | true,x -> Some (x.Trim('\"'))
        | _ -> None

    let operatingSystemRestriction = 
        match kvPairs.TryGetValue "os" with
        | true,x -> Some (x.Trim('\"'))
        | _ -> None

    let packagePath = 
        match kvPairs.TryGetValue "packages" with
        | true,x -> Some (x.Trim('\"'))
        | _ -> None

    let url = url.TrimEnd '/'
    let url = 
        match url.Split ' ' |> Array.toList with 
        | [url; commit] -> url
        | _ -> url

    let origin =
        match url with
        | String.StartsWith @"file:///" _ ->
            LocalGitOrigin url
        | _ ->
            RemoteGitOrigin url

    let server =
        match origin with
        | LocalGitOrigin _ ->
            "localfilesystem"
        | _ ->
            match url.Replace(":","/").LastIndexOf('/') with 
            | -1 -> url
            | pos -> url.Substring(0, pos)
                        
    let server = 
        match server.IndexOf("://") with
        | -1 -> server
        | pos -> server.Substring(pos + 3).Replace(":","") |> removeInvalidChars
        |> fun s -> s.Replace("git@","").Replace(":","/").TrimStart('/')

    let project = url.Substring(url.LastIndexOf('/')+1).Replace(".git","")
    let project = if Directory.Exists project then Path.GetFileName project else project

    server,commit,project,origin,buildCommand,operatingSystemRestriction,packagePath

let getHash repoFolder commitish = 
    try
        if System.IO.Directory.Exists repoFolder |> not then None else
        Some (CommandHelper.runSimpleGitCommand repoFolder ("rev-parse --verify " + commitish))
    with
    | _ -> None

let getCurrentHash repoFolder = 
    getHash repoFolder "HEAD"

let getHashFromRemote url branch =
    let result =
        let hash = CommandHelper.runSimpleGitCommand "" (sprintf "ls-remote %s %s" url branch)
        if String.IsNullOrWhiteSpace hash then
            branch
        else
            hash
    if result.Contains "\t" then
        result.Substring(0,result.IndexOf '\t')
    else
        if result.Contains " " then
            result.Substring(0,result.IndexOf ' ')
        else
            result


let fetchCache repoCacheFolder cloneUrl =
    try
        if not <| Directory.Exists repoCacheFolder then
            if not <| Directory.Exists Constants.GitRepoCacheFolder then
                Directory.CreateDirectory Constants.GitRepoCacheFolder |> ignore
            tracefn "Cloning %s to %s" cloneUrl repoCacheFolder
            CommandHelper.runSimpleGitCommand Constants.GitRepoCacheFolder ("clone " + quote cloneUrl) |> ignore
        else
            CommandHelper.runSimpleGitCommand repoCacheFolder ("remote set-url origin " + quote cloneUrl) |> ignore
            verbosefn "Fetching %s to %s" cloneUrl repoCacheFolder 
        
        CommandHelper.runSimpleGitCommand repoCacheFolder "fetch -f --tags" |> ignore
    with
    | exn -> failwithf "Fetching the git cache at %s failed.%sMessage: %s" repoCacheFolder Environment.NewLine exn.Message

let updateCache repoCacheFolder tempBranchName cloneUrl commit =
    fetchCache repoCacheFolder cloneUrl 
    try
        CommandHelper.runSimpleGitCommand repoCacheFolder ("branch -f " + tempBranchName + " " + commit) |> ignore
    with
    | exn -> failwithf "Updating the git cache at %s failed.%sMessage: %s" repoCacheFolder Environment.NewLine exn.Message

let checkoutToPaketFolder repoFolder cloneUrl cacheCloneUrl commit =
    try
        // checkout to local folder
        if Directory.Exists repoFolder then
            CommandHelper.runSimpleGitCommand repoFolder ("remote set-url origin " + quote cacheCloneUrl) |> ignore
            verbosefn "Fetching %s to %s" cacheCloneUrl repoFolder 
            CommandHelper.runSimpleGitCommand repoFolder "fetch origin -f" |> ignore
        else
            let destination = DirectoryInfo(repoFolder).Parent.FullName
            if not <| Directory.Exists destination then
                Directory.CreateDirectory destination |> ignore
            verbosefn "Cloning %s to %s" cacheCloneUrl repoFolder
            CommandHelper.runSimpleGitCommand destination ("clone " + quote cacheCloneUrl) |> ignore
            CommandHelper.runSimpleGitCommand repoFolder ("remote add upstream " + quote cloneUrl) |> ignore

        tracefn "Setting %s to %s" repoFolder commit
        CommandHelper.runSimpleGitCommand repoFolder ("reset --hard " + commit) |> ignore
    with
    | exn -> failwithf "Checkout to %s failed.%sMessage: %s" repoFolder Environment.NewLine exn.Message