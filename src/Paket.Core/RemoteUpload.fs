﻿module Paket.RemoteUpload

open System
open System.Globalization
open System.IO
open System.Net
open System.Text
open Paket
open Paket.Logging

let GetUrlWithEndpoint (url: string option) (endPoint: string option) =
    let (|UrlWithEndpoint|_|) url = 
        match url with
        | Some url when not (String.IsNullOrEmpty(Uri(url).AbsolutePath.TrimStart('/'))) -> Some(Uri(url)) 
        | _                                                                              -> None  

    let (|IsUrl|_|) (url: string option) =
        match url with
        | Some url -> Uri(url.TrimEnd('/') + "/") |> Some
        | _        -> None
    
    let defaultEndpoint = "/api/v2/package" 
    let urlWithEndpoint = 
        match (url, endPoint) with
        | None                   , _                   -> Uri(Uri("https://nuget.org"), defaultEndpoint)
        | IsUrl baseUrl          , Some customEndpoint -> Uri(baseUrl, customEndpoint.TrimStart('/'))
        | UrlWithEndpoint baseUrl, _                   -> baseUrl
        | IsUrl baseUrl          , None                -> Uri(baseUrl, defaultEndpoint)
        | Some whyIsThisNeeded   , _                   -> failwith "Url and endpoint combination not supported"  
    urlWithEndpoint.ToString ()

  
let Push maxTrials url apiKey packageFileName =
    let tracefnVerbose m = Printf.kprintf traceVerbose m
    let rec push trial =
        if not (File.Exists packageFileName) then
            failwithf "The package file %s does not exist." packageFileName
        tracefn "Pushing package %s to %s - trial %d" packageFileName url trial
        try
            let authOpt = ConfigFile.GetAuthentication(url)
            match authOpt with
            | Some (Auth.Credentials (u,_)) -> 
                tracefnVerbose "Authorizing using credentials for user %s" u
            | Some (Auth.Token _) -> 
                tracefnVerbose "Authorizing using token"
            | None ->
                tracefnVerbose "No authorization found in config file."
            let client = Utils.createWebClient(url, authOpt)
            Utils.addHeader client "X-NuGet-ApiKey" apiKey

            client.UploadFileAsMultipart (new Uri(url)) packageFileName
            |> ignore

            tracefn "Pushing %s complete." packageFileName
        with
        | exn when trial = 1 && exn.Message.Contains("(409)") ->
            failwithf "Package %s already exists." packageFileName
        | exn when trial < maxTrials ->            
            if exn.Message.Contains("(409)") |> not then // exclude conflicts
                match exn with
                | :? WebException as we when not (isNull we.Response) -> 
                    let response = (exn :?> System.Net.WebException).Response
                    use reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8)
                    let text = reader.ReadToEnd()
                    tracefnVerbose "Response body was: %s" text
                    tracefnVerbose "Response: %A" response
                | _ -> ()
                traceWarnfn "Could not push %s: %s" packageFileName exn.Message
                push (trial + 1)

    push 1
