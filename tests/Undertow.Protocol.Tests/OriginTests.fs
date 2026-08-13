module Undertow.Protocol.Tests.OriginTests

open Expecto
open Undertow.Protocol.Origin

[<Tests>]
let originTests =
    testList
        "Origin"
        [
            testList
                "fromEnv"
                [
                    testCase "empty is same-origin"
                    <| fun () -> Expect.equal (fromEnv "") SameOrigin ""
                    testCase "whitespace is same-origin"
                    <| fun () -> Expect.equal (fromEnv "   ") SameOrigin ""
                    testCase "star disables checking"
                    <| fun () -> Expect.equal (fromEnv "*") AllowAll ""

                    testCase "comma list parses and trims"
                    <| fun () ->
                        Expect.equal
                            (fromEnv " https://a.example , https://b.example ,")
                            (AllowList [ "https://a.example"; "https://b.example" ])
                            ""
                ]

            testList
                "allowed: AllowAll"
                [
                    testCase "admits anything"
                    <| fun () ->
                        Expect.isTrue (allowed AllowAll "https://evil.example" "h") ""
                        Expect.isTrue (allowed AllowAll null null) ""
                ]

            testList
                "allowed: AllowList"
                [
                    let policy = AllowList [ "https://app.example" ]

                    testCase "admits an exact match"
                    <| fun () -> Expect.isTrue (allowed policy "https://app.example" "any") ""

                    testCase "rejects a non-member"
                    <| fun () -> Expect.isFalse (allowed policy "https://evil.example" "any") ""

                    testCase "rejects no-Origin (explicit list = explicit match)"
                    <| fun () -> Expect.isFalse (allowed policy null "any") ""
                ]

            testList
                "allowed: SameOrigin"
                [
                    testCase "admits no-Origin (non-browser clients)"
                    <| fun () -> Expect.isTrue (allowed SameOrigin null "localhost:3000") ""

                    testCase "admits a matching origin"
                    <| fun () ->
                        Expect.isTrue
                            (allowed SameOrigin "http://localhost:3000" "localhost:3000")
                            ""

                    testCase "is case-insensitive on authority"
                    <| fun () ->
                        Expect.isTrue
                            (allowed SameOrigin "http://LOCALHOST:3000" "localhost:3000")
                            ""

                    testCase "rejects a cross-origin browser upgrade"
                    <| fun () ->
                        Expect.isFalse
                            (allowed SameOrigin "https://evil.example" "localhost:3000")
                            ""

                    testCase "rejects a port mismatch"
                    <| fun () ->
                        Expect.isFalse
                            (allowed SameOrigin "http://localhost:4000" "localhost:3000")
                            ""

                    testCase "fails closed with no Host"
                    <| fun () -> Expect.isFalse (allowed SameOrigin "http://localhost:3000" null) ""
                ]

            testList
                "sameOrigin"
                [
                    testCase "strips scheme and compares authority"
                    <| fun () ->
                        Expect.isTrue (sameOrigin "https://h.example:8443" "h.example:8443") ""

                    testCase "opaque origin never matches"
                    <| fun () -> Expect.isFalse (sameOrigin "null" "h.example") ""

                    testCase "malformed origin never matches"
                    <| fun () -> Expect.isFalse (sameOrigin "not-a-url" "h.example") ""

                    testCase "path cannot smuggle an authority"
                    <| fun () ->
                        Expect.isFalse (sameOrigin "https://evil.example/h.example" "h.example") ""

                    testCase "empty authority never matches"
                    <| fun () -> Expect.isFalse (sameOrigin "https://" "h.example") ""

                    testCase "host whitespace is trimmed"
                    <| fun () -> Expect.isTrue (sameOrigin "http://h.example" " h.example ") ""
                ]
        ]
