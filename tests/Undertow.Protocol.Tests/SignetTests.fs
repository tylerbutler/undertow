module Undertow.Protocol.Tests.SignetTests

open Expecto
open Undertow.Protocol
open Undertow.Protocol.Signet

let private makeClaims tenantId documentId scopes exp : TokenClaims =
    {
        DocumentId = documentId
        Scopes = scopes
        TenantId = tenantId
        User =
            {
                Id = "test-user"
                Properties = Map.empty
            }
        IssuedAt = 1000L
        Expiration = exp
        Version = "1.0"
        Jti = None
    }

[<Tests>]
let signetTests =
    testList
        "Signet"
        [
            testList
                "claim validation"
                [
                    testCase "validate expiration valid"
                    <| fun () ->
                        let claims = makeClaims "tenant" "doc" [ DocRead ] 2000L
                        Expect.isOk (validateExpiration claims 1500L) "not expired"

                    testCase "validate expiration expired"
                    <| fun () ->
                        let claims = makeClaims "tenant" "doc" [ DocRead ] 1000L

                        match validateExpiration claims 1500L with
                        | Error(TokenExpired(exp, current)) ->
                            Expect.equal exp 1000L "expired at"
                            Expect.equal current 1500L "current"
                        | other -> failtest $"expected TokenExpired, got {other}"

                    testCase "validate tenant match"
                    <| fun () ->
                        let claims = makeClaims "my-tenant" "doc" [ DocRead ] 2000L
                        Expect.isOk (validateTenant claims "my-tenant") "match"

                    testCase "validate tenant mismatch"
                    <| fun () ->
                        let claims = makeClaims "my-tenant" "doc" [ DocRead ] 2000L

                        match validateTenant claims "other-tenant" with
                        | Error(TenantMismatch(token, request)) ->
                            Expect.equal token "my-tenant" "token tenant"
                            Expect.equal request "other-tenant" "request tenant"
                        | other -> failtest $"expected TenantMismatch, got {other}"

                    testCase "validate document match"
                    <| fun () ->
                        let claims = makeClaims "tenant" "my-doc" [ DocRead ] 2000L
                        Expect.isOk (validateDocument claims "my-doc") "match"

                    testCase "validate document mismatch"
                    <| fun () ->
                        let claims = makeClaims "tenant" "my-doc" [ DocRead ] 2000L

                        match validateDocument claims "other-doc" with
                        | Error(DocumentMismatch(token, request)) ->
                            Expect.equal token "my-doc" "token doc"
                            Expect.equal request "other-doc" "request doc"
                        | other -> failtest $"expected DocumentMismatch, got {other}"

                    testCase "validate scope present"
                    <| fun () ->
                        let claims = makeClaims "tenant" "doc" [ DocRead; DocWrite ] 2000L
                        Expect.isOk (validateScope claims DocRead) "read"
                        Expect.isOk (validateScope claims DocWrite) "write"

                    testCase "validate scope missing"
                    <| fun () ->
                        let claims = makeClaims "tenant" "doc" [ DocRead ] 2000L

                        match validateScope claims DocWrite with
                        | Error(MissingScope(required, _)) ->
                            Expect.equal required DocWrite "required"
                        | other -> failtest $"expected MissingScope, got {other}"

                    testCase "has scope helpers"
                    <| fun () ->
                        let claims = makeClaims "tenant" "doc" [ DocRead; DocWrite ] 2000L
                        Expect.isTrue (hasScope claims DocRead) "read"
                        Expect.isTrue (hasScope claims DocWrite) "write"
                        Expect.isFalse (hasScope claims SummaryWrite) "summary"
                        Expect.isTrue (hasReadScope claims) "hasReadScope"
                        Expect.isTrue (hasWriteScope claims) "hasWriteScope"
                        Expect.isFalse (hasSummaryWriteScope claims) "hasSummaryWriteScope"

                    testCase "validate connection claims ok"
                    <| fun () ->
                        let claims = makeClaims "my-tenant" "my-doc" [ DocRead; DocWrite ] 2000L

                        Expect.isOk
                            (validateConnectionClaims claims "my-tenant" "my-doc" 1500L)
                            "ok"

                    testCase "validate connection claims expired first"
                    <| fun () ->
                        let claims = makeClaims "my-tenant" "my-doc" [ DocRead ] 1000L

                        match validateConnectionClaims claims "my-tenant" "my-doc" 1500L with
                        | Error(TokenExpired _) -> ()
                        | other -> failtest $"expected TokenExpired, got {other}"

                    testCase "validate connection claims tenant mismatch"
                    <| fun () ->
                        let claims = makeClaims "my-tenant" "my-doc" [ DocRead ] 2000L

                        match validateConnectionClaims claims "other-tenant" "my-doc" 1500L with
                        | Error(TenantMismatch _) -> ()
                        | other -> failtest $"expected TenantMismatch, got {other}"

                    testCase "validate read access"
                    <| fun () ->
                        let claims = makeClaims "tenant" "doc" [ DocRead ] 2000L
                        Expect.isOk (validateReadAccess claims "tenant" "doc" 1500L) "ok"

                    testCase "validate read access missing scope"
                    <| fun () ->
                        let claims = makeClaims "tenant" "doc" [ DocWrite ] 2000L

                        match validateReadAccess claims "tenant" "doc" 1500L with
                        | Error(MissingScope(required, _)) ->
                            Expect.equal required DocRead "required"
                        | other -> failtest $"expected MissingScope, got {other}"

                    testCase "validate write access"
                    <| fun () ->
                        let claims = makeClaims "tenant" "doc" [ DocRead; DocWrite ] 2000L
                        Expect.isOk (validateWriteAccess claims "tenant" "doc" 1500L) "ok"

                    testCase "validate write access missing write scope"
                    <| fun () ->
                        let claims = makeClaims "tenant" "doc" [ DocRead ] 2000L

                        match validateWriteAccess claims "tenant" "doc" 1500L with
                        | Error(MissingScope(required, _)) ->
                            Expect.equal required DocWrite "required"
                        | other -> failtest $"expected MissingScope, got {other}"

                    testCase "validate summary access"
                    <| fun () ->
                        let claims = makeClaims "tenant" "doc" [ DocRead; SummaryWrite ] 2000L
                        Expect.isOk (validateSummaryAccess claims "tenant" "doc" 1500L) "ok"

                    testCase "format error"
                    <| fun () ->
                        Expect.equal
                            (formatError (TokenExpired(1000L, 1500L)))
                            "Token expired at 1000 (current time: 1500)"
                            "exact message"

                    testCase "error to http code"
                    <| fun () ->
                        Expect.equal (errorToHttpCode (TokenExpired(0L, 0L))) 401 "expired"
                        Expect.equal (errorToHttpCode (TenantMismatch("", ""))) 403 "tenant"
                        Expect.equal (errorToHttpCode (DocumentMismatch("", ""))) 403 "document"
                        Expect.equal (errorToHttpCode (MissingScope(DocRead, []))) 403 "scope"
                        Expect.equal (errorToHttpCode (MissingClaim "")) 401 "missing claim"
                        Expect.equal (errorToHttpCode (InvalidClaim("", ""))) 401 "invalid claim"
                ]

            testList
                "scope conversions"
                [
                    testCase "scope string roundtrip"
                    <| fun () ->
                        for scope in [ DocRead; DocWrite; SummaryRead; SummaryWrite ] do
                            Expect.equal
                                (scopeFromString (scopeToString scope))
                                (Some scope)
                                "roundtrip"

                    testCase "scopes from strings drops unknown"
                    <| fun () ->
                        Expect.equal
                            (scopesFromStrings [ "doc:read"; "bogus"; "summary:write" ])
                            [ DocRead; SummaryWrite ]
                            "dropped"
                ]

            testList
                "crypto"
                [
                    testCase "mint and verify signature roundtrip"
                    <| fun () ->
                        let token =
                            mintToken
                                "tenant-1"
                                "doc-1"
                                [ DocRead; DocWrite ]
                                "user-1"
                                "secret"
                                1000L
                                3600L
                                "test-jti"

                        match verifySignature token "secret" with
                        | Ok claims ->
                            Expect.equal claims.TenantId "tenant-1" "tenant"
                            Expect.equal claims.DocumentId "doc-1" "doc"
                            Expect.equal claims.User.Id "user-1" "user"
                            Expect.equal claims.Expiration 4600L "exp"
                            Expect.equal claims.Scopes [ DocRead; DocWrite ] "scopes"
                        | Error e -> failtest $"expected Ok, got {e}"

                    testCase "verify rejects wrong secret"
                    <| fun () ->
                        let token = mintToken "t" "d" [ DocRead ] "u" "right" 1000L 3600L "j"

                        Expect.equal (verifySignature token "wrong") (Error BadSignature) "rejected"

                    testCase "verify rejects empty secret"
                    <| fun () ->
                        let token = mintToken "t" "d" [ DocRead ] "u" "right" 1000L 3600L "j"
                        Expect.equal (verifySignature token "") (Error BadSignature) "rejected"

                    testCase "verify rejects malformed token"
                    <| fun () ->
                        Expect.equal
                            (verifySignature "not-a-jwt" "secret")
                            (Error BadFormat)
                            "rejected"

                    testCase "verify rejects non-HS256 alg"
                    <| fun () ->
                        // {"alg":"none","typ":"JWT"} base64url + arbitrary payload/sig
                        let header =
                            System.Convert
                                .ToBase64String(
                                    System.Text.Encoding.UTF8.GetBytes
                                        """{"alg":"none","typ":"JWT"}"""
                                )
                                .TrimEnd('=')

                        Expect.equal
                            (verifySignature $"{header}.e30.sig" "secret")
                            (Error BadFormat)
                            "alg none rejected"

                    testCase "extract token parses bearer and basic schemes"
                    <| fun () ->
                        Expect.equal (extractToken "Bearer abc.def.ghi") (Ok "abc.def.ghi") "bearer"

                        Expect.equal
                            (extractToken "Basic abc.def.ghi")
                            (Ok "abc.def.ghi")
                            "basic jwt"

                        Expect.equal (extractToken "Nonsense") (Error BadFormat) "rejected"

                    testCase "extract token decodes basic user:jwt"
                    <| fun () ->
                        let encoded =
                            System.Convert.ToBase64String(
                                System.Text.Encoding.UTF8.GetBytes "user:abc.def.ghi"
                            )

                        Expect.equal (extractToken $"Basic {encoded}") (Ok "abc.def.ghi") "decoded"

                    testCase "validate write access end to end"
                    <| fun () ->
                        let token =
                            mintToken
                                "tenant-1"
                                "doc-1"
                                [ DocRead; DocWrite ]
                                "user-1"
                                "secret"
                                1000L
                                3600L
                                "j"

                        match verifySignature token "secret" with
                        | Ok claims ->
                            Expect.isOk
                                (validateWriteAccess claims "tenant-1" "doc-1" 2000L)
                                "valid"

                            Expect.isError
                                (validateWriteAccess claims "other" "doc-1" 2000L)
                                "wrong tenant"
                        | Error e -> failtest $"expected Ok, got {e}"
                ]
        ]
