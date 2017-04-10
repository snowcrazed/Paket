#if INTERACTIVE
System.IO.Directory.SetCurrentDirectory __SOURCE_DIRECTORY__
#r "../../packages/test/NUnit/lib/net45/nunit.framework.dll"
#r "../../packages/build/FAKE/tools/Fakelib.dll"
#r "../../packages/Chessie/lib/net40/Chessie.dll"
#r "../../bin/paket.core.dll"
#load "../../paket-files/test/forki/FsUnit/FsUnit.fs"
#load "TestHelper.fs"
open Paket.IntegrationTests.TestHelpers
#else
module Paket.IntegrationTests.ResolverSkipsConflictsFastSpecs
#endif

open Fake
open Paket
open System
open NUnit.Framework
open FsUnit
open System
open System.IO
open Paket.Domain

[<Test>]
let ``#1166 Should resolve Nancy without timeout``() =
    let lockFile = update "i001166-resolve-nancy-fast"
    lockFile.Groups.[Constants.MainDependencyGroup].Resolution.[PackageName "Nancy"].Version
    |> shouldBeGreaterThan (SemVer.Parse "1.1")

[<Test>]
[<Ignore("fails with SO, skipping until works")>]
let ``#1174 Should find Ninject error``() =
    updateShouldFindPackageConflict "Ninject" "i001174-resolve-fast-conflict"

#if INTERACTIVE
;;

#endif