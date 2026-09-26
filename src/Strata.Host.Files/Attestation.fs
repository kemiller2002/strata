namespace Strata.Host.Files

open System
open System.Security.Cryptography
open System.Text
open Strata.Semantic.Wire
open Strata.Application

/// Saying who produced an artifact, and proving it was not edited afterwards.
///
/// Authority for: the integrity digest and the signature that wrap an artifact.
///
/// ## Two different claims, deliberately separated
///
/// **Integrity** answers "is this the artifact that was compiled?" It is an
/// unkeyed digest, always written, and it catches the ordinary failure: a file
/// edited by hand, truncated by a bad copy, or merged badly. It proves nothing
/// about WHO produced it — anyone who changes the content can recompute it.
///
/// **A signature** answers "did the holder of this key produce it?" It is
/// optional, needs a private key, and is the only one of the two that resists
/// someone who WANTS to change the artifact.
///
/// Reporting them as one thing would be the more convenient lie. A digest that
/// verifies tells an operator nothing about provenance, and an operator who
/// believes it does has a false sense of what their pipeline guarantees.
///
/// ## What signing actually buys, stated plainly
///
/// `WI-0089` frames this as turning approval "from a flag an agent can pass into
/// an artifact it cannot forge". That is true exactly to the degree that the
/// agent cannot read the private key, and not one bit further. A signature made
/// by a key sitting in the same working directory as the agent proves only that
/// something in that directory signed it.
///
/// So the property is a CUSTODY property, and Strata cannot enforce it. What
/// Strata can do is make the check unavoidable once someone has arranged
/// custody: `--require-signature` refuses an artifact that is unsigned or signed
/// by a key the verifier does not hold, and says which.
///
/// ## What is signed
///
/// The canonical body — `Artifact.toText`, which excludes this wrapper. Two
/// consequences, both wanted: the digest cannot cover itself, and a signature
/// survives a change in how the wrapper is laid out. It works because rendering
/// is deterministic (NFR-001) and reading is exact, so a verifier reconstructs
/// the same bytes the signer signed rather than trusting the file's own
/// formatting.
///
/// ECDSA over P-256 with SHA-256, because it is in the base class library. No
/// package reference, and nothing here is novel cryptography — the interesting
/// decisions above are all about what is claimed, not about the primitive.
module Attestation =

    [<Literal>]
    let DigestAlgorithm = "sha-256"

    [<Literal>]
    let SignatureAlgorithm = "ecdsa-p256-sha256"

    /// What an artifact file carries about its own provenance.
    [<NoComparison>]
    type Wrapper =
        { /// Hex SHA-256 of the canonical body. Absent only in an artifact
          /// written before integrity existed.
          Digest: string option
          /// Base64 signature over the same bytes, and the key it names.
          Signature: (string * string) option
          /// Who compiled, signed or approved it: a Praxis provenance record,
          /// kept as read so every field survives a rewrite
          /// (DF-STRATA-2026-E4B7). Self-reported, OUTSIDE the signed body:
          /// adding it changes neither the digest nor the signature. `None`
          /// for an artifact that never carried one; nothing is invented.
          Provenance: ProvenanceJson.Reading option
          /// Wrapper members this build does not know, in file order. Carried
          /// through `sign` rather than dropped.
          Extra: (string * System.Text.Json.Nodes.JsonNode) list }

    let none = { Digest = None; Signature = None; Provenance = None; Extra = [] }

    [<Literal>]
    let ProvenanceMember = "provenance"

    /// The reference a provenance record uses for the artifact it describes:
    /// the digest of the canonical body, so a record cannot describe one
    /// artifact while sitting in another's wrapper unnoticed.
    let subjectOf (digest: string) = "strata:artifact/sha256:" + digest

    let private toHex (bytes: byte array) =
        bytes |> Array.fold (fun (sb: StringBuilder) b -> sb.Append(b.ToString "x2")) (StringBuilder()) |> string

    /// The bytes everything here covers: the artifact's canonical body.
    let private bodyBytes (resolved: ResolvedDesiredState) =
        Encoding.UTF8.GetBytes(Artifact.toText resolved)

    let digestOf (resolved: ResolvedDesiredState) =
        use sha = SHA256.Create()
        toHex (sha.ComputeHash(bodyBytes resolved))

    /// Sign with a PEM-encoded EC private key.
    let sign (privateKeyPem: string) (resolved: ResolvedDesiredState) : Result<string, string> =
        try
            use key = ECDsa.Create()
            key.ImportFromPem(privateKeyPem.ToCharArray())
            Ok(Convert.ToBase64String(key.SignData(bodyBytes resolved, HashAlgorithmName.SHA256)))
        with ex ->
            Microsoft.FSharp.Core.Error(sprintf "could not sign with that key (%s)" ex.Message)

    /// Verify against a PEM-encoded EC public key.
    let verify (publicKeyPem: string) (resolved: ResolvedDesiredState) (signature: string) : Result<unit, string> =
        try
            use key = ECDsa.Create()
            key.ImportFromPem(publicKeyPem.ToCharArray())

            if key.VerifyData(bodyBytes resolved, Convert.FromBase64String signature, HashAlgorithmName.SHA256) then
                Ok()
            else
                Microsoft.FSharp.Core.Error
                    "the signature does not match this artifact and this key. Either the artifact was changed after \
                     it was signed, or it was signed by a different key."
        with ex ->
            Microsoft.FSharp.Core.Error(sprintf "the signature could not be checked (%s)" ex.Message)

    /// Generate a key pair, PEM-encoded, as (private, public).
    let generateKeyPair () =
        use key = ECDsa.Create ECCurve.NamedCurves.nistP256
        String(key.ExportECPrivateKeyPem()), String(key.ExportSubjectPublicKeyInfoPem())

    /// The file an artifact is stored as: the wrapper, then the body.
    ///
    /// The body is embedded rather than concatenated so the file stays a single
    /// JSON document that any tool can read.
    let renderFile (wrapper: Wrapper) (resolved: ResolvedDesiredState) =
        // Rendered member by member rather than as one `Json` value because the
        // provenance record and unknown members are carried as the JSON they
        // were read as, which `Wire.Json` cannot hold. Without them this is
        // byte-for-byte the rendering `Json.render (JObject members)` gave.
        let memberText (name: string) (valueText: string) = Json.render (JString name) + ":" + valueText

        [ match wrapper.Digest with
          | Some d ->
              memberText "integrity" (Json.render (JObject [ "algorithm", JString DigestAlgorithm; "digest", JString d ]))
          | None -> ()

          match wrapper.Signature with
          | Some (algorithm, value) ->
              memberText "signature" (Json.render (JObject [ "algorithm", JString algorithm; "value", JString value ]))
          | None -> ()

          match wrapper.Provenance with
          | Some reading -> memberText ProvenanceMember (ProvenanceJson.toText (ProvenanceJson.raw reading))
          | None -> ()

          for name, node in wrapper.Extra do
              memberText name (ProvenanceJson.toText node)

          memberText "artifact" (Artifact.toText resolved) ]
        |> String.concat ","
        |> fun body -> "{" + body + "}"

    /// Read an artifact file, returning what it claims about itself alongside it.
    ///
    /// Accepts a bare artifact too — one with no wrapper — so a file written by
    /// an older build still reads. Whether that is ACCEPTABLE is the caller's
    /// decision, not this function's: `--require-signature` is where a policy
    /// about provenance belongs.
    let readFile (text: string) : Result<ResolvedDesiredState * Wrapper, string> =
        try
            use document = System.Text.Json.JsonDocument.Parse text
            let root = document.RootElement

            match root.TryGetProperty "artifact" with
            | false, _ ->
                // No wrapper: the whole document is the artifact.
                Artifact.ofText text |> Result.map (fun r -> r, none)
            | true, body ->
                let read (name: string) (field: string) =
                    match root.TryGetProperty name with
                    | true, element ->
                        match element.TryGetProperty field with
                        | true, v -> Some(v.GetString())
                        | _ -> None
                    | _ -> None

                let known = set [ "integrity"; "signature"; ProvenanceMember; "artifact" ]

                let extra =
                    root.EnumerateObject()
                    |> Seq.filter (fun p -> not (known.Contains p.Name))
                    |> Seq.map (fun p -> p.Name, System.Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText()))
                    |> Seq.toList

                // Malformed provenance is REFUSED, not dropped: a record that
                // cannot be read cannot be carried forward honestly, and
                // silently removing it would erase who produced the artifact.
                let provenance =
                    match root.TryGetProperty ProvenanceMember with
                    | false, _ -> Ok None
                    | true, element ->
                        match ProvenanceJson.validate (System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText())) with
                        | Ok reading -> Ok(Some reading)
                        | Microsoft.FSharp.Core.Error problems ->
                            Microsoft.FSharp.Core.Error(
                                "this artifact's provenance record is malformed ("
                                + ProvenanceJson.describeProblems problems
                                + "). It was not written by this build of Strata as it stands; restore it rather than deleting it.")

                let wrapper provenance =
                    { Digest = read "integrity" "digest"
                      Signature =
                        match read "signature" "algorithm", read "signature" "value" with
                        | Some algorithm, Some value -> Some(algorithm, value)
                        | _ -> None
                      Provenance = provenance
                      Extra = extra }

                match provenance, Artifact.ofText (body.GetRawText()) with
                | Microsoft.FSharp.Core.Error message, _
                | _, Microsoft.FSharp.Core.Error message -> Microsoft.FSharp.Core.Error message
                | Ok provenance, Ok resolved ->
                    let wrapper = wrapper provenance
                    let actual = digestOf resolved

                    let describesAnother =
                        match provenance with
                        | Some (ProvenanceJson.Reading.Current (record, _))
                        | Some (ProvenanceJson.Reading.Unversioned (record, _)) ->
                            record.Subject |> Option.exists (fun subject -> subject <> subjectOf actual)
                        | _ -> false

                    match wrapper.Digest with
                    | Some claimed when claimed <> actual ->
                        Microsoft.FSharp.Core.Error
                            "this artifact does not match its own integrity digest. It was changed after it was \
                             compiled. Recompile it rather than deploying it."
                    | _ when describesAnother ->
                        Microsoft.FSharp.Core.Error
                            "this artifact's provenance record describes a different artifact (its subject is not \
                             this artifact's digest). Provenance is never moved between artifacts."
                    | _ -> Ok(resolved, wrapper)
        with ex ->
            Microsoft.FSharp.Core.Error(sprintf "the artifact could not be read (%s)" ex.Message)

    /// Whether a caller's provenance requirement is met.
    ///
    /// Separated from reading because it is a POLICY, and a policy stated in one
    /// place can be read; one spread through three commands cannot.
    let provenanceProblem (requireSignature: bool) (publicKeyPem: string option) (resolved: ResolvedDesiredState) (wrapper: Wrapper) =
        match wrapper.Signature, publicKeyPem with
        | None, _ when requireSignature ->
            Some
                "this artifact is not signed, and --require-signature was given. Sign it with `strata sign`, \
                 using a key whoever is meant to authorise deployments holds."
        | Some _, None when requireSignature ->
            Some
                "this artifact is signed and --require-signature was given, but no --public-key was supplied to \
                 check the signature against. A signature nobody verifies is decoration."
        | Some (algorithm, _), Some _ when algorithm <> SignatureAlgorithm ->
            Some(
                sprintf
                    "this artifact is signed with '%s', which this build cannot check. It reads '%s'."
                    algorithm
                    SignatureAlgorithm)
        | Some (_, value), Some pem ->
            match verify pem resolved value with
            | Ok () -> None
            | Microsoft.FSharp.Core.Error message -> Some message
        | _ -> None
