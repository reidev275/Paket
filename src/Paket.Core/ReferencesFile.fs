﻿namespace Paket

open System
open System.IO
open Logging
open Paket.Domain
open Paket.Requirements

type RemoteFileReference = 
    { Name : string
      Link : string
      Settings : RemoteFileInstallSettings }

type PackageInstallSettings = 
    { Name : PackageName
      Settings : InstallSettings }

    static member Default(name) =
        { Name = PackageName name
          Settings = InstallSettings.Default }

type ReferencesFile = 
    { FileName: string
      NugetPackages: PackageInstallSettings list
      RemoteFiles: RemoteFileReference list } 
    
    static member DefaultLink = Constants.PaketFilesFolderName

    static member New(fileName) = 
        { FileName = fileName
          NugetPackages = []
          RemoteFiles = [] }

    static member FromLines(lines : string[]) = 
        let isSingleFile (line: string) = line.StartsWith "File:"
        let notEmpty (line: string) = not <| String.IsNullOrWhiteSpace line
        let parsePackageInstallSettings (line: string) = 
            let parts = line.Split(' ')            
            { Name = PackageName parts.[0]
              Settings = InstallSettings.Parse(line.Replace(parts.[0],"")) }

        let remoteLines,nugetLines =
            lines 
            |> Array.filter notEmpty 
            |> Array.map (fun s -> s.Trim())
            |> Array.toList
            |> List.partition isSingleFile 

        { FileName = ""
          NugetPackages =
            nugetLines
            |> List.map parsePackageInstallSettings
          RemoteFiles = 
            remoteLines
            |> List.map (fun s -> s.Replace("File:","").Split([|' '|], StringSplitOptions.RemoveEmptyEntries))
            |> List.map (fun segments ->
                            let hasPath =
                                let get x = if segments.Length > x then segments.[x] else ""
                                segments.Length >= 2 && not ((get 1).Contains(":")) && not ((get 2).StartsWith(":")) 

                            let rest = 
                                let skip = if hasPath then 2 else 1
                                if segments.Length < skip then "" else String.Join(" ",segments |> Seq.skip skip)

                            { Name = segments.[0]
                              Link = if hasPath then segments.[1] else ReferencesFile.DefaultLink 
                              Settings = RemoteFileInstallSettings.Parse rest } ) }

    static member FromFile(fileName : string) =
        let lines = File.ReadAllLines(fileName)
        { ReferencesFile.FromLines lines with FileName = fileName }

    member this.AddNuGetReference(packageName : PackageName, copyLocal: bool, importTargets: bool, frameworkRestrictions, includeVersionInPath, omitContent : bool) =
        let (PackageName referenceName) = packageName
        let normalized = NormalizedPackageName packageName
        if this.NugetPackages |> Seq.exists (fun p -> NormalizedPackageName p.Name = normalized) then
            this
        else
            tracefn "Adding %s to %s" referenceName this.FileName      

            let package =
                { Name = packageName
                  Settings = 
                    { CopyLocal = if not copyLocal then Some copyLocal else None
                      ImportTargets = if not importTargets then Some importTargets else None
                      FrameworkRestrictions = frameworkRestrictions
                      IncludeVersionInPath = if includeVersionInPath then Some includeVersionInPath else None
                      OmitContent = if omitContent then Some omitContent else None } }

            { this with NugetPackages = this.NugetPackages @ [ package ] }

    member this.AddNuGetReference(packageName : PackageName) = this.AddNuGetReference(packageName, true, true, [], false, false)

    member this.RemoveNuGetReference(packageName : PackageName) =
        let (PackageName referenceName) = packageName
        let normalized = NormalizedPackageName packageName
        if this.NugetPackages |> Seq.exists (fun p -> NormalizedPackageName p.Name = normalized) |> not then
            this
        else
            tracefn "Removing %s from %s" referenceName (this.FileName)
            { this with NugetPackages = this.NugetPackages |> List.filter (fun p -> NormalizedPackageName p.Name <> normalized) }

    member this.Save() =
        File.WriteAllText(this.FileName, this.ToString())
        tracefn "References file saved to %s" this.FileName

    override this.ToString() =
        List.append
            (this.NugetPackages |> List.map (fun p ->                
                String.Join(" ",[p.Name.ToString(); p.Settings.ToString()] |> List.filter (fun s -> s <> ""))))
            (this.RemoteFiles |> List.map (fun s -> "File:" + s.Name + if s.Link <> ReferencesFile.DefaultLink then " " + s.Link else ""))
            |> String.concat Environment.NewLine