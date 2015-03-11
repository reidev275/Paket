﻿module Paket.RemoteUpload

open System
open System.Globalization
open System.IO
open System.Net
open System.Text
open Paket
open Paket.Logging
open FSharp.Control.Reactive

type System.Net.WebClient with
        member x.UploadFileAsMultipart (url:Uri) filename =
            let fileTemplate = "--{0}\r\nContent-Disposition: form-data; name=\"{1}\"; filename=\"{2}\"\r\nContent-Type: {3}\r\n\r\n"
            let boundary = "---------------------------" + DateTime.Now.Ticks.ToString("x", CultureInfo.InvariantCulture)
            let fileInfo = (new FileInfo(Path.GetFullPath(filename)))
            let fileHeaderBytes = String.Format(CultureInfo.InvariantCulture, fileTemplate, boundary, "package", "package", "application/octet-stream")
                                  |> Encoding.UTF8.GetBytes
            let newlineBytes = Environment.NewLine |> Encoding.UTF8.GetBytes
            let trailerbytes = String.Format(CultureInfo.InvariantCulture, "--{0}--", boundary) |> Encoding.UTF8.GetBytes
            x.Headers.Add(HttpRequestHeader.ContentType, "multipart/form-data; boundary=" + boundary);
            use stream = x.OpenWrite(url, "PUT")
            stream.Write(fileHeaderBytes,0,fileHeaderBytes.Length)
            use fileStream = File.OpenRead fileInfo.FullName
            fileStream.CopyTo(stream, (4*1024))
            stream.Write(newlineBytes, 0, newlineBytes.Length)
            stream.Write(trailerbytes, 0, trailerbytes.Length)
            ()

        member x.UploadFileAsMultipartAsync (url:Uri) filename = 
            
            // event to report back completion of upload
            let progressReport = new Event<int64 * int64>()
            
            let computation = async {
                let fileTemplate = "--{0}\r\nContent-Disposition: form-data; name=\"{1}\"; filename=\"{2}\"\r\nContent-Type: {3}\r\n\r\n"
                let boundary = "---------------------------" + DateTime.Now.Ticks.ToString("x", CultureInfo.InvariantCulture)
                let fileInfo = (new FileInfo(Path.GetFullPath(filename)))
                let fileHeaderBytes = String.Format(CultureInfo.InvariantCulture, fileTemplate, boundary, "package", "package", "application/octet-stream")
                                      |> Encoding.UTF8.GetBytes
                let newlineBytes = Environment.NewLine |> Encoding.UTF8.GetBytes
                let trailerbytes = String.Format(CultureInfo.InvariantCulture, "--{0}--", boundary) |> Encoding.UTF8.GetBytes
                x.Headers.Add(HttpRequestHeader.ContentType, "multipart/form-data; boundary=" + boundary);
                use stream = x.OpenWrite(url, "PUT")
                do! stream.AsyncWrite(fileHeaderBytes,0,fileHeaderBytes.Length)
                use fileStream = File.OpenRead fileInfo.FullName
                
                // upload file content and report progress
                do!
                    let fileSize = fileInfo.Length
                    let bufferSize = 4*1024
                    let buffer = Array.zeroCreate bufferSize
                                         
                    let rec upload bytesWritten = async {
                        let! count = fileStream.AsyncRead(buffer, 0 ,bufferSize)
                        if count > 0 then 
                            if count < bufferSize then
                                do! stream.AsyncWrite(Array.sub buffer 0 count)
                            else
                                do! stream.AsyncWrite(buffer) 
                                do progressReport.Trigger(bytesWritten, fileSize) 
                            return! upload (bytesWritten + (count |> int64))
                    }
                    upload 0L

                do! stream.AsyncWrite(newlineBytes, 0, newlineBytes.Length)
                do! stream.AsyncWrite(trailerbytes, 0, trailerbytes.Length)
            }
            computation, progressReport.Publish

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
    let rec push trial = async {
        tracefn "Pushing package %s to %s - trial %d" packageFileName url trial
        try
            let client = Utils.createWebClient(url, None)
            client.Headers.Add("X-NuGet-ApiKey", apiKey)
            let uploadAsync, progressReport = client.UploadFileAsMultipartAsync (new Uri(url)) packageFileName
            use progressSubscription = 
                progressReport 
                |> Observable.sample (TimeSpan.FromSeconds(1.))
                |> Observable.subscribe 
                    (fun (bytesWritten, fileSize) ->
                         tracefn @"Pushing %s:  %i/%i KB uploaded " packageFileName (bytesWritten / 1024L) (fileSize / 1024L))
            do! uploadAsync
            tracefn "Pushing %s complete." packageFileName
        with
        | exn when trial < maxTrials ->             
            traceWarnfn "Could not push %s: %s" packageFileName exn.Message            
            return! push (trial + 1)
    }

    push 1 |> Async.RunSynchronously