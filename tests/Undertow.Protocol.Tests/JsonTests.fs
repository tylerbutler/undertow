module Undertow.Protocol.Tests.JsonTests

open Expecto
open Undertow.Protocol

[<Tests>]
let jsonTests =
    testList
        "Json"
        [
            testCase "object key order is author order"
            <| fun () ->
                let json = JObj [ "b", JInt 1L; "a", JInt 2L ]
                Expect.equal (Json.toString json) """{"b":1,"a":2}""" "author order preserved"

            testCase "canonicalize sorts keys recursively"
            <| fun () ->
                let json = JObj [ "b", JObj [ "z", JNull; "a", JNull ]; "a", JInt 2L ]

                Expect.equal
                    (Json.toString (Json.canonicalize json))
                    """{"a":2,"b":{"a":null,"z":null}}"""
                    "recursive sort"

            testCase "non-ASCII is raw UTF-8, not escaped"
            <| fun () ->
                let json = JObj [ "name", JStr "Ünïcödé <&>" ]

                Expect.equal
                    (Json.toString json)
                    "{\"name\":\"Ünïcödé <&>\"}"
                    "matches Erlang json:encode"

            testCase "raw splice is verbatim"
            <| fun () ->
                let raw = System.Text.Encoding.UTF8.GetBytes """{"z":1,"a":2}"""
                let json = JArr [ JRaw(System.ReadOnlyMemory raw) ]
                Expect.equal (Json.toString json) """[{"z":1,"a":2}]""" "no re-encode"

            testCase "output is never indented"
            <| fun () ->
                let json = JObj [ "a", JArr [ JInt 1L; JInt 2L ] ]
                Expect.isFalse ((Json.toString json).Contains "\n") "compact"
        ]
