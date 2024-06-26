﻿namespace UmamusumeStoryDataExtractor

open System
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
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

[<JsonConverter(typeof<TextBlockConverter>)>]
type TextBlock = {
    Name: string
    Text: string
    ChoiceDataList: string[]
    ColorTextInfoList: string[]
    Siblings: ValueOption<TextBlock[]>
}

and TextBlockConverter() =
    inherit JsonConverter<TextBlock>()

    override _.Read(reader: byref<Utf8JsonReader>, typeToConvert: Type, options: JsonSerializerOptions): TextBlock =
        raise (NotImplementedException())

    override this.Write(writer: Utf8JsonWriter, value: TextBlock, options: JsonSerializerOptions) =
        writer.WriteStartObject()
        writer.WriteString("Name", value.Name)
        writer.WriteString("Text", value.Text)

        writer.WriteStartArray("ChoiceDataList")
        for choiceData in value.ChoiceDataList do
            writer.WriteStringValue(choiceData)
        writer.WriteEndArray()

        writer.WriteStartArray("ColorTextInfoList")
        for colorTextInfo in value.ColorTextInfoList do
            writer.WriteStringValue(colorTextInfo)
        writer.WriteEndArray()

        match value.Siblings with
        | ValueSome siblings ->
            writer.WriteStartArray("Siblings")
            for sibling in siblings do
                this.Write(writer, sibling, options)
            writer.WriteEndArray()
        | _ -> ()

        writer.WriteEndObject()

[<JsonConverter(typeof<TextDataConverter>)>]
type TextData =
    | StoryData of Title: string * TextBlockList: ValueOption<TextBlock>[]
    | RaceData of textData: string[]

and TextDataConverter() =
    inherit JsonConverter<TextData>()
    
    override _.Read(reader: byref<Utf8JsonReader>, typeToConvert: Type, options: JsonSerializerOptions): TextData =
        raise (NotImplementedException())

    override _.Write(writer: Utf8JsonWriter, value: TextData, options: JsonSerializerOptions) =
        match value with
        | StoryData (title, textBlockList) ->
            writer.WriteStartObject()
            writer.WriteString("Title", title)
            writer.WritePropertyName("TextBlockList")
            writer.WriteStartArray()
            for textBlock in textBlockList do
                match textBlock with
                | ValueSome block -> JsonSerializer.Serialize(writer, block, options)
                | _ -> writer.WriteNullValue()
            writer.WriteEndArray()
            writer.WriteEndObject()
        | RaceData textData ->
            writer.WriteStartArray()
            for text in textData do
                writer.WriteStringValue(text)
            writer.WriteEndArray()

module Program =
    let StoryTimelinePattern =
        Regex(@"story/data/\d+/\d+/storytimeline_\d+", RegexOptions.Compiled)

    let HomeTimelinePattern =
        Regex(@"home/data/(\d+)/(\d+)/hometimeline_\1_\2_\d+", RegexOptions.Compiled)

    let RaceTimelinePattern =
        Regex(@"race/storyrace/text/storyrace_\d+", RegexOptions.Compiled)

    let GetTextSourcePaths dir =
        use connection = new SQLiteConnection(Path.Combine(dir, "meta"))

        connection.Query<TextDataPair>("select n, h from a")
        |> Seq.map (fun textData ->
            match textData.Name with
            | name when StoryTimelinePattern.IsMatch(name) || HomeTimelinePattern.IsMatch(name) -> Some(Story(textData))
            | name when RaceTimelinePattern.IsMatch(name) -> Some(Race(textData))
            | _ -> None)
        |> Seq.choose id

    [<EntryPoint>]
    let main args =
        if args.Length < 2 || args.Length > 3 then
            Console.WriteLine "Usage: UmamusumeStoryDataExtractor <UmamusumeGameDataDirectoryPath> <OutputPath> [HashJsonDirectory]"
            1
        else
            let dataDir = args[0]
            let outputDir = args[1]
            let hashMap =
                if args.Length = 3 then
                    let mergedMap = Collections.Generic.Dictionary<uint64, string>()
                    let maps =
                        Directory.EnumerateFiles(args[2], "*.json", SearchOption.AllDirectories)
                        |> Seq.map(fun file ->
                            use fileStream = new FileStream(file, FileMode.Open, FileAccess.Read)
                            try
                                JsonSerializer.Deserialize<Collections.Generic.Dictionary<string, string>>(fileStream)
                            with
                            | _ -> null
                        )
                        |> Seq.where (isNull >> not)
                    Seq.fold(fun (resultMap: Collections.Generic.Dictionary<uint64, string>) (map: Collections.Generic.Dictionary<string, string>) ->
                        for kv in map do
                            let succeed, hash = UInt64.TryParse(kv.Key)
                            if succeed then
                                resultMap.TryAdd(hash, kv.Value) |> ignore
                            else
                                ()
                        resultMap) mergedMap maps
                else
                    null

            let mayLocalize str =
                if hashMap = null then
                    str
                else
                    let found, var = hashMap.TryGetValue(CppUtility.GetCppStdHash(str))
                    if found then
                        var
                    else
                        str

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

                Console.CancelKeyPress.AddHandler (fun _ e ->
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

                                    let mutable mainScript: Collections.Specialized.OrderedDictionary = null
                                    let textClipData = Collections.Generic.Dictionary<int64, Collections.Specialized.OrderedDictionary>()

                                    for o in loadedFile.Objects
                                        |> Seq.filter (fun o -> o :? MonoBehaviour)
                                        |> Seq.cast<MonoBehaviour>
                                        do
                                        if o.m_Name = mainScriptName then
                                            mainScript <- o.ToType()
                                        else
                                            let succeed, script = o.m_Script.TryGet()
                                            if succeed && script.m_Name = "StoryTimelineTextClipData" then
                                                textClipData.Add(o.m_PathID, o.ToType())

                                    let result =
                                        match textData with
                                        | Story _ ->
                                            let title = mayLocalize(mainScript["Title"] :?> string)
                                            let blockList =
                                                (mainScript["BlockList"] :?> Collections.Generic.IList<obj>)
                                                |> Seq.cast<Collections.Specialized.OrderedDictionary>
                                                |> Seq.sortBy (fun obj -> obj["BlockIndex"] :?> int)
                                                |> Seq.map (fun obj ->
                                                    (obj["TextTrack"] :?> Collections.Specialized.OrderedDictionary)["ClipList"] :?> Collections.Generic.IList<obj>)
                                                |> Seq.map (fun clipList ->
                                                    if clipList.Count = 0 then
                                                        ValueNone
                                                    else
                                                        let firstClip = clipList[0]
                                                        let getTextClipContent (textClip: obj) =
                                                            let textClip = textClipData[(textClip :?> Collections.Specialized.OrderedDictionary)["m_PathID"] :?> int64]
                                                            let name = mayLocalize(textClip["Name"] :?> string)
                                                            let text = mayLocalize(textClip["Text"] :?> string)
                                                            let choiceDataList =
                                                                textClip["ChoiceDataList"] :?> Collections.Generic.IList<obj>
                                                                |> Seq.cast<Collections.Specialized.OrderedDictionary>
                                                                |> Seq.map (fun obj -> mayLocalize(obj["Text"] :?> string))
                                                                |> Seq.toArray
                                                            let colorTextInfoList =
                                                                textClip["ColorTextInfoList"] :?> Collections.Generic.IList<obj>
                                                                |> Seq.cast<Collections.Specialized.OrderedDictionary>
                                                                |> Seq.map (fun obj -> mayLocalize(obj["Text"] :?> string))
                                                                |> Seq.toArray
                                                            name, text, choiceDataList, colorTextInfoList
                                                        let name, text, choiceDataList, colorTextInfoList = getTextClipContent firstClip
                                                        let siblings =
                                                            if clipList.Count > 1 then
                                                                ValueSome(clipList
                                                                |> Seq.skip 1
                                                                |> Seq.map (fun clip ->
                                                                    let name, text, choiceDataList, colorTextInfoList = getTextClipContent clip
                                                                    { Name = name; Text = text; ChoiceDataList = choiceDataList; ColorTextInfoList = colorTextInfoList; Siblings = ValueNone }
                                                                )
                                                                |> Seq.toArray)
                                                            else
                                                                ValueNone
                                                        ValueSome { Name = name; Text = text; ChoiceDataList = choiceDataList; ColorTextInfoList = colorTextInfoList; Siblings = siblings }
                                                    )
                                                |> Seq.toArray
                                            Some(StoryData(title, blockList))
                                        | Race _ -> Some(RaceData(
                                            (mainScript["textData"] :?> System.Collections.Generic.IList<obj>)
                                            |> Seq.cast<System.Collections.Specialized.OrderedDictionary>
                                            |> Seq.sortBy (fun obj -> obj["key"] :?> int)
                                            |> Seq.map (fun obj -> mayLocalize(obj["text"] :?> string))
                                            |> Seq.toArray))

                                    use outputFile = new FileStream(resultPath, FileMode.OpenOrCreate)

                                    let options =
                                        JsonWriterOptions(
                                            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                                            Indented = true
                                        )

                                    use writer = new Utf8JsonWriter(outputFile, options)
                                    JsonSerializer.Serialize(writer, result)

                                    progressBar.Tick($"Extracted {data.Name}.json")
                    )
                    |> ignore
                with
                | :? OperationCanceledException -> progressBar.WriteErrorLine("Extraction interrupted by user request")

                0
            else
                Console.WriteLine $"Game data directory {dataDir} does not exist"
                1
