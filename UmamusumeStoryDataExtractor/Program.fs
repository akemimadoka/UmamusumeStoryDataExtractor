namespace UmamusumeStoryDataExtractor

open System
open System.IO
open System.Text.Json
open System.Text.Encodings.Web
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open AssetStudio
open SQLite
open ShellProgressBar

type TextDataPair() =
    [<Column("n")>]
    member val Name: string = null with get, set

    [<Column("h")>]
    member val Id: string = null with get, set

type TextSource =
    | Story of TextDataPair
    | Race of TextDataPair

    member this.TextData =
        match this with
        | Story textData -> textData
        | Race textData -> textData

module Program =
    let StoryTimelinePattern =
        Regex(@"story/data/\d+/\d+/storytimeline_\d+", RegexOptions.Compiled)

    let RaceTimelinePattern =
        Regex(@"race/storyrace/text/storyrace_\d+", RegexOptions.Compiled)

    let GetTextSourcePaths dir =
        use connection = new SQLiteConnection(Path.Combine(dir, "meta"))

        connection.Query<TextDataPair>("select n, h from a")
        |> Seq.map (fun textData ->
            match textData.Name with
            | name when StoryTimelinePattern.IsMatch(name) -> Some(Story(textData))
            | name when RaceTimelinePattern.IsMatch(name) -> Some(Race(textData))
            | _ -> None)
        |> Seq.choose id

    [<EntryPoint>]
    let main args =
        if args.Length <> 2 then
            Console.WriteLine "Usage: UmamusumeStoryDataExtractor <UmamusumeGameDataDirectoryPath> <OutputPath>"
            1
        else
            let dataDir = args[0]
            let outputDir = args[1]

            if Directory.Exists(dataDir) then
                let textSources = GetTextSourcePaths dataDir |> Seq.toArray
                Console.WriteLine $"Discovered {textSources.Length} stories"
                Console.WriteLine "Press Ctrl-C to interrupt extraction"

                use progressBar = new ProgressBar(textSources.Length, "Extracting story data...")

                use cancellationTokenSource = new CancellationTokenSource()

                let option =
                    ParallelOptions(
                        CancellationToken = cancellationTokenSource.Token,
                        MaxDegreeOfParallelism = Environment.ProcessorCount
                    )

                Console.CancelKeyPress.AddHandler (fun sender e ->
                    cancellationTokenSource.Cancel()
                    e.Cancel <- true)

                try
                    Parallel.ForEach(
                        textSources,
                        option,
                        fun (textData: TextSource) ->
                            let data = textData.TextData
                            let resultPath = Path.Combine(outputDir, data.Name + ".json")

                            if File.Exists(resultPath) then
                                progressBar.Tick($"Skipping existing {data.Name}.json")
                            else
                                Directory.CreateDirectory(Path.GetDirectoryName(resultPath))
                                |> ignore

                                let mainScriptName = Path.GetFileName(data.Name)
                                // 会做额外操作，还是一个个加载吧。。
                                let assetManager = AssetsManager()

                                let path = Path.Combine(dataDir, "dat", data.Id.Substring(0, 2), data.Id)

                                if File.Exists(path) |> not then
                                    progressBar.Tick($"Skipping not downloaded {data.Name}.json")
                                else
                                    assetManager.LoadFiles(path)

                                    let loadedFile = assetManager.assetsFileList[0]

                                    let textDataSeq =
                                        loadedFile.Objects
                                        |> Seq.filter (fun o -> o :? MonoBehaviour)
                                        |> Seq.cast<MonoBehaviour>
                                        |> Seq.map (fun o ->
                                            if o.m_Name = mainScriptName then
                                                let typeTree = o.ToType()

                                                match textData with
                                                | Story _ -> [ typeTree["Title"] :?> string ] :> seq<string>
                                                | Race _ ->
                                                    (typeTree["textData"] :?> System.Collections.Generic.IList<obj>)
                                                    |> Seq.map (fun obj ->
                                                        (obj :?> System.Collections.Specialized.OrderedDictionary)["text"]
                                                        :?> string)
                                            else
                                                let succeed, script = o.m_Script.TryGet()

                                                if succeed
                                                   && script.m_Name = "StoryTimelineTextClipData" then
                                                    let typeTree = o.ToType()

                                                    let result =
                                                        [ typeTree["Name"] :?> string
                                                          typeTree["Text"] :?> string ]

                                                    let choiceDataList =
                                                        typeTree["ChoiceDataList"]
                                                        :?> System.Collections.Generic.IList<obj>

                                                    if choiceDataList = null then
                                                        result
                                                    else
                                                        result
                                                        |> Seq.append (
                                                            choiceDataList
                                                            |> Seq.map (fun (obj) ->
                                                                (obj
                                                                :?> System.Collections.Specialized.OrderedDictionary)["Text"]
                                                                :?> string)
                                                        )
                                                else
                                                    [])
                                        |> Seq.concat
                                        |> Seq.distinct
                                        |> Seq.map (fun text -> CppUtility.GetCppStdHash(text).ToString(), text)

                                    use outputFile = new FileStream(resultPath, FileMode.OpenOrCreate)

                                    let options =
                                        JsonWriterOptions(
                                            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                                            Indented = true
                                        )

                                    use writer = new Utf8JsonWriter(outputFile, options)
                                    writer.WriteStartObject()

                                    for (hash, text) in textDataSeq do
                                        writer.WritePropertyName(hash)
                                        writer.WriteStringValue(text)

                                    writer.WriteEndObject()

                                    progressBar.Tick($"Extracted {data.Name}.json")
                    )
                    |> ignore
                with
                | :? OperationCanceledException -> progressBar.WriteErrorLine("Extraction interrupted by user request")

                0
            else
                Console.WriteLine $"Game data directory {dataDir} does not exist"
                1
