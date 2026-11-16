publish() {
    # compression: the bundled dlls are zipped inside the exe and unpacked into memory at startup
    # trimming: drops framework code the app never calls. json uses source generation (Models/JsonContext.cs) so it survives
    # TrimmerRootAssembly: never trim our own code, avalonia xaml can reach it in ways the trimmer cannot see
    # InvariantGlobalization: no culture data (icu), the app has no localized text
    # DebugType=none: no .pdb next to the exe
    dotnet publish -c Release -r "$1" --self-contained true \
        -p:PublishSingleFile=true \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:EnableCompressionInSingleFile=true \
        -p:PublishTrimmed=true \
        -p:TrimmerRootAssembly=DungeonMapper \
        -p:InvariantGlobalization=true \
        -p:DebugType=none
}

publish "win-x64"
publish "linux-x64"
