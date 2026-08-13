namespace Undertow.Protocol

/// Port of floodgate/origin: the CSWSH policy shared by both socket endpoints.
/// Pure — callers extract the Origin and Host headers and pass them in
/// (null = header absent).
module Origin =

    type OriginPolicy =
        /// Admit only same-origin upgrades, plus clients that send no Origin
        /// at all (non-browser clients cannot be driven into a cross-site
        /// upgrade).
        | SameOrigin
        /// Admit only these exact origins; no Origin means no match.
        | AllowList of string list
        /// Disable origin checking entirely.
        | AllowAll

    /// Parse the ALLOWED_ORIGINS value: empty = same-origin, `*` disables
    /// checking, anything else is a comma-separated allow-list.
    let fromEnv (value: string) : OriginPolicy =
        match value.Trim() with
        | "" -> SameOrigin
        | "*" -> AllowAll
        | origins ->
            origins.Split(',')
            |> Array.map (fun o -> o.Trim())
            |> Array.filter (fun o -> o <> "")
            |> List.ofArray
            |> AllowList

    let private originAuthority (origin: string) : string option =
        match origin.IndexOf("://", System.StringComparison.Ordinal) with
        | -1 -> None
        | idx ->
            let rest = origin.Substring(idx + 3)

            let authority =
                match rest.IndexOf '/' with
                | -1 -> rest
                | slash -> rest.Substring(0, slash)

            if authority = "" then
                None
            else
                Some(authority.ToLowerInvariant())

    /// Same-origin rule: strip the scheme, compare the authority (host plus
    /// any port) case-insensitively. Malformed/opaque origins never match.
    let sameOrigin (origin: string) (host: string) : bool =
        match originAuthority origin with
        | Some authority -> authority = host.Trim().ToLowerInvariant()
        | None -> false

    /// Whether an upgrade carrying these headers is admissible.
    /// Pass null for an absent header.
    let allowed (policy: OriginPolicy) (origin: string | null) (host: string | null) : bool =
        match policy with
        | AllowAll -> true
        | AllowList origins ->
            match origin with
            | null -> false
            | origin -> List.contains origin origins
        | SameOrigin ->
            match origin, host with
            | null, _ -> true
            | _, null -> false
            | origin, host -> sameOrigin origin host

/// C#-friendly wrapper holding a parsed policy.
type OriginPolicyBox private (policy: Origin.OriginPolicy) =
    static member FromEnv(value: string) = OriginPolicyBox(Origin.fromEnv value)
    member _.Allowed(origin: string | null, host: string | null) = Origin.allowed policy origin host
