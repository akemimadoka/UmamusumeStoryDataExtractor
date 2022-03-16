namespace UmamusumeStoryDataExtractor

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open AssetStudio
open SQLite
open ShellProgressBar

type StoryDataPair() =
    [<Column("n")>]
    member val Name: string = null with get, set

    [<Column("h")>]
    member val Id: string = null with get, set

module Program =
    let StoryTimelinePattern =
        Regex(@"story/data/\d+/\d+/storytimeline_\d+", RegexOptions.Compiled)

    let GetStoryDataPaths dir =
        use connection = new SQLiteConnection(Path.Combine(dir, "meta"))

        connection.Query<StoryDataPair>("select n, h from a")
        |> Seq.filter (fun storyData -> StoryTimelinePattern.IsMatch(storyData.Name))

    [<EntryPoint>]
    let main args =
        if args.Length <> 2 then
            Console.WriteLine "Usage: UmamusumeStoryDataExtractor <UmamusumeGameDataDirectoryPath> <OutputPath>"
            1
        else
            let dataDir = args[0]
            let outputDir = args[1]

            if Directory.Exists(dataDir) then
                let stories = GetStoryDataPaths dataDir |> Seq.toArray
                Console.WriteLine $"Discovered {stories.Length} stories"

                use progressBar = new ProgressBar(stories.Length, "Extracting story data...")

                Parallel.ForEach(
                    stories,
                    fun (storyData: StoryDataPair) ->
                        let resultPath = Path.Combine(outputDir, storyData.Name + ".json")

                        Directory.CreateDirectory(Path.GetDirectoryName(resultPath))
                        |> ignore

                        let mainScriptName = Path.GetFileName(storyData.Name)
                        // 会做额外操作，还是一个个加载吧。。
                        let assetManager = AssetsManager()
                        assetManager.LoadFiles(Path.Combine(dataDir, "dat", storyData.Id.Substring(0, 2), storyData.Id))
                        let loadedFile = assetManager.assetsFileList[0]

                        let textData =
                            loadedFile.Objects
                            |> Seq.filter (fun (o) -> o :? MonoBehaviour)
                            |> Seq.cast<MonoBehaviour>
                            |> Seq.map (fun (o) ->
                                if o.m_Name = mainScriptName then
                                    let typeTree = o.ToType()
                                    [ typeTree["Title"] :?> string ] :> seq<string>
                                else
                                    let succeed, script = o.m_Script.TryGet()

                                    if succeed
                                       && script.m_Name = "StoryTimelineTextClipData" then
                                        let typeTree = o.ToType()

                                        let result =
                                            [ typeTree["Name"] :?> string
                                              typeTree["Text"] :?> string ]

                                        let choiceDataList =
                                            typeTree["ChoiceDataList"] :?> System.Collections.Generic.IList<obj>

                                        if choiceDataList = null then
                                            result
                                        else
                                            result
                                            |> Seq.append (
                                                choiceDataList
                                                |> Seq.map (fun (obj) ->
                                                    (obj :?> System.Collections.Specialized.OrderedDictionary)["Text"]
                                                    :?> string)
                                            )
                                    else
                                        [])
                            |> Seq.concat
                            |> Seq.distinct

                        use outputFile = new FileStream(resultPath, FileMode.OpenOrCreate)
                        use writer = new StreamWriter(outputFile, Encoding.UTF8)

                        writer.Write('{')

                        for text in textData do
                            let cppStdHash = CppUtility.GetCppStdHash(text)
                            let processedText = Regex.Escape(text)
                            writer.Write($"\"{cppStdHash}\": \"{processedText}\",")

                        writer.Flush()

                        if outputFile.Position > 1 then
                            outputFile.Seek(-1, SeekOrigin.Current) |> ignore // 去除结尾逗号

                        writer.Write('}')

                        progressBar.Tick($"Extracted {storyData.Name}.json")
                )
                |> ignore

                0
            else
                Console.WriteLine $"Game data directory {dataDir} does not exist"
                1
