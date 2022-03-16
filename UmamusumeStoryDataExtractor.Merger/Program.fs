namespace UmamusumeStoryDataExtractor

open System
open System.Text
open System.Text.RegularExpressions
open System.Text.Json
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

            let result = ConcurrentDictionary<string, string>()

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
                        let oldValue = result.GetOrAdd(obj.Name, value)

                        if oldValue <> value then
                            Console.WriteLine $"Text \"{oldValue}\" and \"{obj.Value}\" conflicts!")

                    ()
            )
            |> ignore

            let outputDir = Path.GetDirectoryName(outputJsonPath)

            if outputDir.Length > 0 then
                Directory.CreateDirectory(outputDir) |> ignore

            use outputFile = new FileStream(outputJsonPath, FileMode.OpenOrCreate)
            use writer = new StreamWriter(outputFile, Encoding.UTF8)

            writer.Write('{')

            for kv in result do
                let processedText = Regex.Escape(kv.Value)
                writer.Write($"\"{kv.Key}\": \"{processedText}\",")

            writer.Flush()

            if outputFile.Position > 1 then
                outputFile.Seek(-1, SeekOrigin.Current) |> ignore // 去除结尾逗号

            writer.Write('}')

            0
