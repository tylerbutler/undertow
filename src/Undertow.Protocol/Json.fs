namespace Undertow.Protocol

open System
open System.Buffers
open System.Text.Json

/// Ordered JSON AST, one-for-one with gleam/json: object key order is the wire
/// contract and stays visible in the source.
type Json =
    | JNull
    | JBool of bool
    | JInt of int64
    | JFloat of float
    | JStr of string
    /// Pre-encoded UTF-8 JSON spliced verbatim (raw_json / preprocessed_array).
    | JRaw of ReadOnlyMemory<byte>
    | JArr of Json list
    | JObj of (string * Json) list

module Json =

    let private writerOptions =
        JsonWriterOptions(
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
            SkipValidation = true
        )

    let rec private write (w: Utf8JsonWriter) (json: Json) : unit =
        match json with
        | JNull -> w.WriteNullValue()
        | JBool b -> w.WriteBooleanValue b
        | JInt i -> w.WriteNumberValue i
        | JFloat f -> w.WriteNumberValue f
        | JStr s -> w.WriteStringValue s
        | JRaw bytes -> w.WriteRawValue(bytes.Span, skipInputValidation = true)
        | JArr items ->
            w.WriteStartArray()
            items |> List.iter (write w)
            w.WriteEndArray()
        | JObj fields ->
            w.WriteStartObject()

            fields
            |> List.iter (fun (name, value) ->
                w.WritePropertyName name
                write w value)

            w.WriteEndObject()

    /// Render to UTF-8 bytes into a caller-supplied buffer writer (hot path).
    let writeTo (buffer: IBufferWriter<byte>) (json: Json) : unit =
        use w = new Utf8JsonWriter(buffer, writerOptions)
        write w json

    /// Render to a fresh UTF-8 byte array.
    let toUtf8 (json: Json) : byte[] =
        let buffer = ArrayBufferWriter<byte>()
        writeTo buffer json
        buffer.WrittenSpan.ToArray()

    /// Render to a string (tests and non-hot paths only).
    let toString (json: Json) : string =
        Text.Encoding.UTF8.GetString(toUtf8 json)

    /// Compare two keys by UTF-8 byte ordinal. UTF-16 ordinal comparison differs
    /// from UTF-8 byte order in the U+E000..U+FFFF vs non-BMP range, so compare
    /// the encoded bytes.
    let private compareUtf8 (a: string) (b: string) : int =
        let ba = Text.Encoding.UTF8.GetBytes a
        let bb = Text.Encoding.UTF8.GetBytes b

        let s =
            System.MemoryExtensions.SequenceCompareTo(ReadOnlySpan<byte> ba, ReadOnlySpan<byte> bb)

        s

    /// Equivalent of floodgate's normalize_client_json: recursively sort JObj
    /// keys by UTF-8 ordinal. Mirrors Erlang flatmap term order for <=32 keys.
    let rec canonicalize (json: Json) : Json =
        match json with
        | JObj fields ->
            fields
            |> List.map (fun (k, v) -> k, canonicalize v)
            |> List.sortWith (fun (a, _) (b, _) -> compareUtf8 a b)
            |> JObj
        | JArr items -> JArr(items |> List.map canonicalize)
        | other -> other

/// Thin decode helpers over System.Text.Json's JsonElement, mirroring the Gleam
/// dynamic/decode idiom so ported call sites stay mechanical.
module Dyn =

    let tryParse (utf8: ReadOnlyMemory<byte>) : JsonDocument option =
        try
            Some(JsonDocument.Parse utf8)
        with _ ->
            None

    let tryParseString (text: string) : JsonDocument option =
        try
            Some(JsonDocument.Parse text)
        with _ ->
            None

    let tryField (name: string) (el: JsonElement) : JsonElement option =
        if el.ValueKind = JsonValueKind.Object then
            match el.TryGetProperty name with
            | true, v -> Some v
            | _ -> None
        else
            None

    let stringField (name: string) (el: JsonElement) : string option =
        match tryField name el with
        | Some v when v.ValueKind = JsonValueKind.String -> Some(nonNull (v.GetString()))
        | _ -> None

    let intField (name: string) (el: JsonElement) : int64 option =
        match tryField name el with
        | Some v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetInt64() with
            | true, i -> Some i
            | _ -> None
        | _ -> None

    let boolField (name: string) (el: JsonElement) : bool option =
        match tryField name el with
        | Some v when v.ValueKind = JsonValueKind.True -> Some true
        | Some v when v.ValueKind = JsonValueKind.False -> Some false
        | _ -> None

    let tryObject (el: JsonElement) : JsonElement option =
        if el.ValueKind = JsonValueKind.Object then
            Some el
        else
            None

    let tryArray (el: JsonElement) : JsonElement list option =
        if el.ValueKind = JsonValueKind.Array then
            Some(el.EnumerateArray() |> Seq.toList)
        else
            None

    /// Convert a parsed element to the ordered AST. Object key order is the
    /// document's own order; canonicalize afterwards where the contract wants it.
    let rec toJson (el: JsonElement) : Json =
        match el.ValueKind with
        | JsonValueKind.Null -> JNull
        | JsonValueKind.True -> JBool true
        | JsonValueKind.False -> JBool false
        | JsonValueKind.String -> JStr(nonNull (el.GetString()))
        | JsonValueKind.Number ->
            match el.TryGetInt64() with
            | true, i -> JInt i
            | _ -> JFloat(el.GetDouble())
        | JsonValueKind.Array -> JArr(el.EnumerateArray() |> Seq.map toJson |> Seq.toList)
        | JsonValueKind.Object ->
            JObj(el.EnumerateObject() |> Seq.map (fun p -> p.Name, toJson p.Value) |> Seq.toList)
        | _ -> JNull
