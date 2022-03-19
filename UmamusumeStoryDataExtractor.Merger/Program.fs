namespace UmamusumeStoryDataExtractor

open System
open System.Text
open System.Text.Json
open System.Text.Encodings.Web
open System.IO
open System.Collections.Concurrent
open System.Threading.Tasks

module Program =
    [<EntryPoint>]
    let main args =
        if args.Length <> 2 then
            Console.WriteLine "Usage: UmamusumeStoryDataExtractor.Merger <ExtractedDataDirPath> <OutputJsonPath>"

            1
        else
            let extractedDataDirPath = args[0]
            let outputJsonPath = args[1]

            let result = ConcurrentQueue<string * string>()

            let allFiles =
                Directory.GetFiles(extractedDataDirPath, "*", SearchOption.AllDirectories)
                |> Seq.filter (fun filename -> Path.GetExtension(filename) = ".json")

            Parallel.ForEach(
                allFiles,
                fun filename ->
                    use fileStream = new FileStream(filename, FileMode.Open)
                    let doc = JsonDocument.Parse(fileStream)

                    doc.RootElement.EnumerateObject()
                    |> Seq.iter (fun obj ->
                        let value = obj.Value.GetString()
                        result.Enqueue(obj.Name, value)
                    )
            )
            |> ignore

            let outputDir = Path.GetDirectoryName(outputJsonPath)

            if outputDir.Length > 0 then
                Directory.CreateDirectory(outputDir) |> ignore

            use outputFile = new FileStream(outputJsonPath, FileMode.OpenOrCreate)
            let options = JsonWriterOptions(
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Indented = true
            )
            use writer = new Utf8JsonWriter(outputFile, options)
            writer.WriteStartObject()
            for (hash, text) in result |> Seq.distinctBy(fun (k, _) -> k) do
                writer.WritePropertyName(hash)
                writer.WriteStringValue(text)
            writer.WriteEndObject()

            0
