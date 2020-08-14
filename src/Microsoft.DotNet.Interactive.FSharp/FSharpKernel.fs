// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DotNet.Interactive.FSharp

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
open System.Threading.Tasks

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.Tags
open Microsoft.CodeAnalysis.Text
open Microsoft.DotNet.Interactive
open Microsoft.DotNet.Interactive.Commands
open Microsoft.DotNet.Interactive.Events
open Microsoft.DotNet.Interactive.Extensions

open FSharp.Compiler.Interactive.Shell
open FSharp.Compiler.Scripting
open FSharp.Compiler.SourceCodeServices

[<AbstractClass>]
type FSharpKernelBase () as this =

    inherit DotNetKernel("fsharp")

    static let lockObj = Object();

    let createScript () =  
        lock lockObj (fun () -> new FSharpScript(additionalArgs=[|"/langversion:preview"|]))

    let script = lazy createScript ()

    let extensionLoader: AssemblyBasedExtensionLoader = AssemblyBasedExtensionLoader()

    let mutable cancellationTokenSource = new CancellationTokenSource()

    let kindString (glyph: FSharpGlyph) =
        match glyph with
        | FSharpGlyph.Class -> WellKnownTags.Class
        | FSharpGlyph.Constant -> WellKnownTags.Constant
        | FSharpGlyph.Delegate -> WellKnownTags.Delegate
        | FSharpGlyph.Enum -> WellKnownTags.Enum
        | FSharpGlyph.EnumMember -> WellKnownTags.EnumMember
        | FSharpGlyph.Event -> WellKnownTags.Event
        | FSharpGlyph.Exception -> WellKnownTags.Class
        | FSharpGlyph.Field -> WellKnownTags.Field
        | FSharpGlyph.Interface -> WellKnownTags.Interface
        | FSharpGlyph.Method -> WellKnownTags.Method
        | FSharpGlyph.OverridenMethod -> WellKnownTags.Method
        | FSharpGlyph.Module -> WellKnownTags.Module
        | FSharpGlyph.NameSpace -> WellKnownTags.Namespace
        | FSharpGlyph.Property -> WellKnownTags.Property
        | FSharpGlyph.Struct -> WellKnownTags.Structure
        | FSharpGlyph.Typedef -> WellKnownTags.Class
        | FSharpGlyph.Type -> WellKnownTags.Class
        | FSharpGlyph.Union -> WellKnownTags.Enum
        | FSharpGlyph.Variable -> WellKnownTags.Local
        | FSharpGlyph.ExtensionMethod -> WellKnownTags.ExtensionMethod
        | FSharpGlyph.Error -> WellKnownTags.Error

    let filterText (declarationItem: FSharpDeclarationListItem) =
        match declarationItem.NamespaceToOpen, declarationItem.Name.Split '.' with
        // There is no namespace to open and the item name does not contain dots, so we don't need to pass special FilterText to Roslyn.
        | None, [|_|] -> null
        // Either we have a namespace to open ("DateTime (open System)") or item name contains dots ("Array.map"), or both.
        // We are passing last part of long ident as FilterText.
        | _, idents -> Array.last idents

    let documentation (declarationItem: FSharpDeclarationListItem) =
        let result = declarationItem.DescriptionTextAsync
        result.ToString()

    let completionItem (declarationItem: FSharpDeclarationListItem) =
        let kind = kindString declarationItem.Glyph
        let filterText = filterText declarationItem
        let documentation = documentation declarationItem
        CompletionItem(declarationItem.Name, kind, filterText=filterText, documentation=documentation)

    let diagnostic (error: FSharpErrorInfo) =
        // F# errors are 1-based but should be 0-based for diagnostics, however, 0-based errors are still valid to report
        let diagLineDelta = if error.Start.Line = 0 then 0 else -1
        let startPos = LinePosition(error.Start.Line + diagLineDelta, error.Start.Column)
        let endPos = LinePosition(error.End.Line + diagLineDelta, error.End.Column)
        let linePositionSpan = LinePositionSpan(startPos, endPos)
        let severity =
            match error.Severity with
            | FSharpErrorSeverity.Error -> DiagnosticSeverity.Error
            | FSharpErrorSeverity.Warning -> DiagnosticSeverity.Warning
        let errorId = sprintf "FS%04i" error.ErrorNumber
        Diagnostic(linePositionSpan, severity, errorId, error.Message)

    let handleSubmitCode (codeSubmission: SubmitCode) (context: KernelInvocationContext) =
        async {
            let codeSubmissionReceived = CodeSubmissionReceived(codeSubmission)
            context.Publish(codeSubmissionReceived)
            let tokenSource = cancellationTokenSource
            let result, errors =
                try
                    script.Value.Eval(codeSubmission.Code, tokenSource.Token)
                with
                | ex -> Error(ex), [||]

            let diagnostics = errors |> Array.map diagnostic
            context.Publish(DiagnosticsProduced(diagnostics, codeSubmission))

            match result with
            | Ok(result) ->
                match result with
                | Some(value) when value.ReflectionType <> typeof<unit>  ->
                    let value = value.ReflectionValue
                    let formattedValues = FormattedValue.FromObject(value)
                    context.Publish(ReturnValueProduced(value, codeSubmission, formattedValues))
                | Some(_) -> ()
                | None -> ()
            | Error(ex) ->
                if not (tokenSource.IsCancellationRequested) then
                    let aggregateError = String.Join("\n", errors)
                    let reportedException =
                        match ex with
                        | :? FsiCompilationException -> CodeSubmissionCompilationErrorException(ex) :> Exception
                        | _ -> ex
                    context.Fail(reportedException, aggregateError)
                else
                    context.Fail(null, "Command cancelled")
        }

    let handleRequestCompletions (requestCompletions: RequestCompletions) (context: KernelInvocationContext) =
        async {
            let! declarationItems = script.Value.GetCompletionItems(requestCompletions.Code, requestCompletions.LinePosition.Line + 1, requestCompletions.LinePosition.Character)
            let completionItems =
                declarationItems
                |> Array.map completionItem
            context.Publish(CompletionsProduced(completionItems, requestCompletions))
        }

    let handleRequestHoverText (requestHoverText: RequestHoverText) (context: KernelInvocationContext) =
        async {
            let! (parse, check, ctx) = script.Value.Fsi.ParseAndCheckInteraction(requestHoverText.Code)
            let t = FSharp.Compiler.Text.SourceText.ofString requestHoverText.Code
            let line = requestHoverText.LinePosition.Line + 1
            let col = requestHoverText.LinePosition.Character
            let lineStr = t.GetLineString(line - 1)

            
            let tokenizer = FSharpSourceTokenizer([], None).CreateLineTokenizer(lineStr)
            let rec tokenizeLine (tokenizer:FSharpLineTokenizer) state =
              match tokenizer.ScanToken(state) with
              | Some tok, state ->
                  if not System.Diagnostics.Debugger.IsAttached then
                    System.Diagnostics.Debugger.Launch() |> ignore
                  if tok.LeftColumn <= col && tok.RightColumn >= col then
                    Some tok
                  else
                    tokenizeLine tokenizer state
              | None, _ -> None

            let token = tokenizeLine tokenizer FSharpTokenizerLexState.Initial

            match token with
            | Some token -> 
                //System.Diagnostics.Debugger.Launch() |> ignore
                let name = lineStr.Substring(token.LeftColumn, token.RightColumn - token.LeftColumn + 1)

                let! (FSharpToolTipText (elements : list<FSharpToolTipElement<string>>)) = check.GetToolTipText(line, token.RightColumn, lineStr, [name], FSharpTokenTag.Identifier)

                let all  = 
                    elements |> List.collect (function
                        | FSharpToolTipElement.Group g ->
                            g |> List.map (fun g ->
                                g.MainDescription
                            )
                        | _ -> []
                    )

                let res = all |> List.toArray |> Array.map (fun str -> FormattedValue("text/markdown", "`" + str + "`"))
                
                let sp = LinePosition(requestHoverText.LinePosition.Line, token.LeftColumn)
                let ep = LinePosition(requestHoverText.LinePosition.Line, token.RightColumn)
                let lps = LinePositionSpan(sp, ep)

                if res.Length > 0 then
                    context.Publish(HoverTextProduced(requestHoverText, res, lps))
                else
                    context.Complete(requestHoverText)
            | None ->
                context.Complete(requestHoverText)
        }


    let handleRequestDiagnostics (requestDiagnostics: RequestDiagnostics) (context: KernelInvocationContext) =
        async {
            let! (_parseResults, checkFileResults, _checkProjectResults) = script.Value.Fsi.ParseAndCheckInteraction(requestDiagnostics.Code)
            let errors = checkFileResults.Errors
            let diagnostics = errors |> Array.map diagnostic
            context.Publish(DiagnosticsProduced(diagnostics, requestDiagnostics))
        }

    let createPackageRestoreContext registerForDisposal =
        let packageRestoreContext = new PackageRestoreContext()
        do registerForDisposal(fun () -> packageRestoreContext.Dispose())
        packageRestoreContext

    let _packageRestoreContext = lazy createPackageRestoreContext this.RegisterForDisposal

    member this.GetCurrentVariables() =
        script.Value.Fsi.GetBoundValues()
        |> List.filter (fun x -> x.Name <> "it") // don't report special variable `it`
        |> List.map (fun x -> CurrentVariable(x.Name, x.Value.ReflectionType, x.Value.ReflectionValue))

    override _.GetVariableNames() =
        this.GetCurrentVariables()
        |> List.map (fun x -> x.Name)
        :> IReadOnlyCollection<string>

    override _.TryGetVariable<'a>(name: string, [<Out>] value: 'a byref) =
        match script.Value.Fsi.TryFindBoundValue(name) with
        | Some cv ->
            value <- cv.Value.ReflectionValue :?> 'a
            true
        | _ ->
            false

    override _.SetVariableAsync(name: string, value: Object) : Task = 
        script.Value.Fsi.AddBoundValue(name, value) |> ignore
        Task.CompletedTask

    member _.RestoreSources = _packageRestoreContext.Value.RestoreSources;

    member _.RequestedPackageReferences = _packageRestoreContext.Value.RequestedPackageReferences;

    member _.ResolvedPackageReferences = _packageRestoreContext.Value.ResolvedPackageReferences;

    member _.PackageRestoreContext = _packageRestoreContext.Value

    // ideally via IKernelCommandHandler<RequestCompletion>, but requires https://github.com/dotnet/fsharp/pull/2867
    member _.HandleRequestCompletionAsync(command: RequestCompletions, context: KernelInvocationContext) = handleRequestCompletions command context |> Async.StartAsTask :> Task

    // ideally via IKernelCommandHandler<RequestDiagnostics, but requires https://github.com/dotnet/fsharp/pull/2867
    member _.HandleRequestDiagnosticsAsync(command: RequestDiagnostics, context: KernelInvocationContext) = handleRequestDiagnostics command context |> Async.StartAsTask :> Task
    
    // ideally via IKernelCommandHandler<RequestHoverText, but requires https://github.com/dotnet/fsharp/pull/2867
    member _.HandleRequestHoverText(command: RequestHoverText, context: KernelInvocationContext) = handleRequestHoverText command context |> Async.StartAsTask :> Task

    // ideally via IKernelCommandHandler<SubmitCode, but requires https://github.com/dotnet/fsharp/pull/2867
    member _.HandleSubmitCodeAsync(command: SubmitCode, context: KernelInvocationContext) = handleSubmitCode command context |> Async.StartAsTask :> Task

    interface ISupportNuget with
        member _.AddRestoreSource(source: string) =
            this.PackageRestoreContext.AddRestoreSource source

        member _.GetOrAddPackageReference(packageName: string, packageVersion: string) =
            this.PackageRestoreContext.GetOrAddPackageReference (packageName, packageVersion)

        member _.RestoreAsync() = 
            this.PackageRestoreContext.RestoreAsync()

        member _.RestoreSources = 
            this.PackageRestoreContext.RestoreSources

        member _.RequestedPackageReferences = 
            this.PackageRestoreContext.RequestedPackageReferences

        member _.ResolvedPackageReferences =
            this.PackageRestoreContext.ResolvedPackageReferences

        member _.RegisterResolvedPackageReferences (packageReferences: IReadOnlyList<ResolvedPackageReference>) =
            // Generate #r and #I from packageReferences
            let sb = StringBuilder()
            let hashset = HashSet()

            for reference in packageReferences do
                for assembly in reference.AssemblyPaths do
                    if hashset.Add(assembly) then
                        if File.Exists assembly then
                            sb.AppendFormat("#r @\"{0}\"", assembly) |> ignore
                            sb.Append(Environment.NewLine) |> ignore

                match reference.PackageRoot with
                | null -> ()
                | root ->
                    if hashset.Add(root) then
                        if File.Exists root then
                            sb.AppendFormat("#I @\"{0}\"", root) |> ignore
                            sb.Append(Environment.NewLine) |> ignore
            let command = new SubmitCode(sb.ToString(), "fsharp")
            this.DeferCommand(command)

    interface IExtensibleKernel with
        member this.LoadExtensionsFromDirectoryAsync(directory:DirectoryInfo, context:KernelInvocationContext) =
            extensionLoader.LoadFromDirectoryAsync(directory, this, context)