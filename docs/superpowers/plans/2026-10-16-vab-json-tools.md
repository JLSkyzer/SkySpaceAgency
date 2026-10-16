# VAB JSON Tools (Export, Import, Parts Dump) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A small "SkySpaceAgency — VAB tools" window, opened from the VAB's AppBar, with three tools:
- **Export**: writes the rocket open in the VAB to `vab/exports/<vessel>_<stamp>.json`. The file holds a readable summary for an AI (Δv, TWR, Isp, thrust and masses per stage, every part, resource totals) and the game's own JSON of the assembly.
- **Import**: loads a mod export, a raw game assembly or a VAB workspace file from `vab/imports/` (or `vab/exports/`) as the main rocket. It refuses, before touching anything, a file with a part the VAB does not offer. It backs up the whole workspace first and restores it if loading fails.
- **Dump parts**: writes every part of the game to `vab/dumps/parts_<campaign>_<stamp>.json`. Each part says whether it is unlocked and which tech node unlocks it, with its raw definition and an example assembly, so an AI can write a valid rocket.

**Architecture:** All the rules live in pure classes under `Code/VabTools/` (namespace `K2D2.VabTools`). They work on plain DTOs and on Newtonsoft `JObject` trees, and EditMode tests cover them:
- `VabJson`: lossless parse and write;
- `VabFiles`: file names, import list;
- `VabNum`: rounding;
- `VabSummary`: the export summary;
- `VabExport`: the export document;
- `VabDump`: the dump document;
- `VabImport`: format detection, structure checks, missing parts, resource clamping;
- `VabSnapshot`: main-assembly swap in a workspace snapshot.

The game-facing code stays thin:
- `KSPService/VabReader` maps the builder to DTOs;
- `KSPService/VabLoader` calls the game's load paths (method A, then method B);
- `KSPService/VabActions` handles the files, the import pipeline and the `[VabTools] …` log lines;
- `UI/VabToolsWindow` is the window, built in code;
- `K2D2_Plugin` adds the VAB AppBar button.

**Tech Stack:** Unity 6000.6.0f1, C# (mod assembly `SkySpaceAgency`), Newtonsoft.Json (package `com.unity.nuget.newtonsoft-json` 3.2.2, already referenced by the mod as `Newtonsoft.Json.dll`), Unity Test Framework (NUnit, EditMode), KSP2 Redux game API (`KSP.OAB`, `KSP.Sim`, `KSP.Sim.DeltaV`, `KSP.IO`, `KSP.Game`), UitkForKsp2 (`Window.Create`), SpaceWarp2 (`Appbar`).

**Spec:** `docs/superpowers/specs/2026-10-16-vab-json-tools-design.md` (read it first, including "Choix de Claude après relecture": it is binding).

**Decisions the spec left to the plan:**
1. **Window built in code, no new UXML.** Only `K2D2_Window.uxml` is an Addressables entry (group `addressables_SkySpaceAgency_all`, address `Assets/UI/K2D2_UI/K2D2_Window.uxml`). The tab UXMLs reach the bundle only as `<ui:Template>` dependencies, so a new UXML file in `Assets/UI/K2D2_UI/` would **not** be picked up. It would need a new Addressables entry, and the project rule is not to hand-edit that YAML. `UitkForKsp2.API.Window.Create(WindowOptions, VisualElement)` (confirmed) takes a code-built tree instead. The window borrows the style sheets of the already-loaded `K2D2_Window.uxml` asset and reuses AppShell's USS class names (`oab-window-root`, `oab-window-header`…). It falls back to inline colors when no sheet is found.
2. **Loading methods**, chosen from the docs:
   - **Method A** (primary when the VAB has a main rocket):
     - take a snapshot of the workspace (`Historian.GenerateSnapshot`);
     - put the imported assembly in place of the `isMainAssembly` entry;
     - call `ActivePartTracker.ClearAll()`, then `UtilFileIO.ApplySnapshot(tracker, snapshot, true)`.

     ApplySnapshot is the path the game itself uses to restore a workspace, and the snapshot keeps the detached assemblies (spec choice 3).
   - **Method B** (fallback, and the only method when the VAB is empty): delete the main rocket (`DeletePart(main.Anchor)`), then call `UtilFileIO.LoadSerializedAssembly`, which "sets it as the main assembly".
   - **After each method**, the result is checked: within 3 s the main rocket must have the imported part count. If it does not, the original snapshot is re-applied before trying the next method. Every step writes a `[VabTools] import: …` line.
3. **Game version** = `Application.version`. `VersionID.VERSION_TEXT` is a `const`, inlined into the mod at compile time, so it would give the build's version, not the game's. **Mod version** = `SWMetadata.SWInfo.Version` (as `AboutUI` does).
4. **Rounding**: masses 3 decimals, Δv, Isp, thrust and burn time 1, TWR 2, altitude and cost 0, resource amounts 3. NaN and infinities become JSON `null`.
5. **Stage order**: the game's `VesselDeltaV.StageInfo` order is kept as is, each entry with its `Stage` number. The export log line lists that order, so in-game test 1 can compare it with the VAB's numbering, which the spec asks to verify.

**Deliberate deviations from the spec:**
1. **A third import format: a VAB workspace file** (`OABHistoricalSnapshot`, with `"Assemblies"`). The `before_import_…` backup is such a snapshot ("instantané de tout l'atelier", spec choice 3), and the spec says the player can always re-import it. Importing a workspace file therefore restores the whole workspace, detached assemblies included. The game's own workspace saves load the same way.
2. **The import list also shows `vab/exports/`** (prefixed `exports/`), besides `vab/imports/`. Without it, in-game tests 2 and 3 and the restore from `before_import_…` would each need a manual file copy.
3. **Clamping also lowers `capacityUnits` above the part definition's capacity**, then the amount to the capacity. An amount can only be bounded by a capacity the file cannot inflate. A part whose JSON carries `PartSwitchOverrides` or `partPropertyOverrides` (variant or procedural) is only checked against its own `capacityUnits`, because its definition capacity may not apply.
4. **More refusals before the rocket is touched** (each keeps the rocket intact, as the spec's anti-objectives require):
   - a broken structure: duplicate `PartGuid`, `RootGuid` that is not a part, or a node pointing to a part that is not in the file;
   - JSON the game cannot deserialize, or deserializes with a different part count;
   - the game's own `OABProvider.AreAllAssemblyPartsUnlocked` returning false.
5. **`unlocked` in the dump means "offered by this VAB"** (`Stats.AvailableParts`), the exact criterion of the import, so spec test 5 holds by construction. The campaign flag `SessionManager.IsPartUnlocked` is given beside it as `campaign_unlocked`.
6. **Extra fields**:
   - `mod_version` in the dump;
   - `dry_mass_t` per part in the export (the spec's `mass_t` is the wet mass: dry plus resources);
   - `part_count` and `unlocked_count` in the dump.

## Global Constraints

- Unity editor: `D:/UnityHub/Editor/6000.6.0f1/Editor/Unity.exe`. Use this exact version; never open the project with another one.
- Project path: `D:/KSPReduxModding/SkySpaceAgency`. Close any interactive Unity editor on this project before a batch run: batch mode fails on a locked project.
- Logs and test results go to the session scratchpad, never into the repository.
- Mod code lives in `Assets/SkySpaceAgency/Code` (assembly `SkySpaceAgency`, root namespace `K2D2`):
  - **Pure code**: namespace `K2D2.VabTools`, folder `Assets/SkySpaceAgency/Code/VabTools/`. Every new type is `public`, so the test assembly can reach it.
  - **Game-facing code**: `Assets/SkySpaceAgency/Code/KSPService/` (namespace `K2D2.KSPService`).
  - **Window**: `Assets/SkySpaceAgency/Code/UI/VabToolsWindow.cs` (namespace `K2D2.UI`).
- **Tests**: a new assembly `SkySpaceAgency.Tests.Vab` in `Assets/Tests/Vab/`, which references `Newtonsoft.Json.dll` (Task 1).
- Line numbers in this plan are those of the files at commit `311fa6d` (start of the plan). Always locate an edit by the quoted code.
- **Files**: `<mod folder>/vab/{exports,imports,dumps}/`, where `<mod folder>` is the folder of `SkySpaceAgency.dll` (`K2D2_Plugin.AssemblyFolder`, the folder of the settings files). Encoding is UTF-8 without BOM; the JSON is indented.
- **UI and log text is in English.**
  - Every log line starts with `[VabTools] `. The import lines start with `[VabTools] import: ` and cover the recognized format, the structure check, the part check, every clamp, the game checks, the backup, each method tried, the restore and the result.
  - The status line uses these messages:
    - `Refused: <reason> Your rocket was not changed.`;
    - `Refused: N part(s) not available in this VAB: a (not unlocked), b (unknown part). Your rocket was not changed.`;
    - `Import failed: <reason>. Your previous rocket was restored (backup: exports/<file>).`
- **Game API members** used by this plan were confirmed in `API-Documentation/beta/xrefmap.yml`, their HTML pages and `xml/9b15c3ff/Assembly-CSharp.xml` (latest beta, 2026-10-16):
  - `GameInstance.OAB : OABProvider`:
    - `OABProvider.Current : ObjectAssemblyBuilder` ("null if the OAB is not open");
    - `OABProvider.AreAllAssemblyPartsUnlocked(SerializedAssembly) : bool`;
  - `GameState.VehicleAssemblyBuilder`;
  - `ObjectAssemblyBuilder` (`KSP.OAB`):
    - `IsLoaded`, `IsShuttingDown`, `Stats : OABSessionInformation`, `UtilFileIO : ObjectAssemblyBuilderFileIO`, `ActivePartTracker : ObjectAssemblyPartTracker`;
    - `Historian : ObjectyAssemblyBuilderHistorian` (sic);
    - `HasEnoughResourcesToLoadAssembly(SerializedAssembly) : bool`;
  - `OABSessionInformation`: `MainAssembly : IObjectAssembly`, `HasMainAssembly`, `AvailableParts` / `AllKnownParts : List<IObjectAssemblyAvailablePart>`, `GetCurrentWorkspaceVehicleDisplayName : string`;
  - `ObjectAssemblyBuilderFileIO`:
    - `AssemblyToString(IObjectAssembly) : string`;
    - `LoadSerializedAssembly(SerializedAssembly)`;
    - `ApplySnapshot(ObjectAssemblyPartTracker, OABHistoricalSnapshot, bool sendMessages = false)`;
  - `ObjectyAssemblyBuilderHistorian`: `GenerateSnapshot(OABActionType type = OABActionType.None) : OABHistoricalSnapshot`, `History : IReadOnlyCollection<OABHistoricalSnapshot>`;
  - `ObjectAssemblyPartTracker`: `ClearAll()`, `DeletePart(IObjectAssemblyPart, bool omitPartDeletedMessages = false)`, `PartsMasterList : IReadOnlyList<IObjectAssemblyPart>`, `PartAssemblyCount() : int`;
  - `OABHistoricalSnapshot.Assemblies : List<OABPlacedAssembly>`, with `OABPlacedAssembly.Assembly : SerializedAssembly`, `isMainAssembly`, `position` and `boundsSize`. `SerializedAssembly.parts : List<SerializedPart>` (`KSP.Sim`);
  - `IObjectAssembly`: `Anchor`, `Parts : List<IObjectAssemblyPart>`, `VesselDeltaV : VesselDeltaVComponent`, `GetWetMass()`, `GetDryMass()`;
  - `IObjectAssemblyPart`: `AvailablePart`, `UniqueId : IGGuid`, `ParentPart`, `MyNodeConnectedToParent : IObjectAssemblyPartNode` (`NodeTag : string`), `SymmetrySet : SymmetrySet` (class, `ID : int`), `Resources : IObjectAssemblyResource[]`. `IObjectAssemblyResource` has `Name`, `Count`, `Capacity` and `TotalMass`, all `float` except `Name`;
  - `IObjectAssemblyAvailablePart`: `Name`, `Title`, `Mass` (dry, t), `PartData : PartData`;
  - `VesselDeltaVComponent`: `IsReady`, `TotalDeltaVVac`, `TotalDeltaVActual`, `StageInfo : List<DeltaVStageInfo>`;
  - `DeltaVStageInfo`:
    - `Stage`, `StartMass`, `EndMass`, `DryMass`, `FuelMass`, `IspVac`, `IspASL`;
    - `ThrustVac`, `ThrustASL`, `TWRVac`, `TWRASL`, `TWRActual`;
    - `DeltaVinVac`, `DeltaVatASL`, `DeltaVActual`, `StageBurnTime`;
    - `Parts : List<DeltaVPartInfo>` (`PartRef : IDeltaVPart`, `GlobalId : IGGuid`), `EnginesActiveInStage : List<DeltaVEngineInfo>` (`Part : IDeltaVPart`);
    - `DeltaVSituation` (`CelestialBody`, `IsAtmosphere`, `Altitude`);
  - `IGGuid.Guid : System.Guid` (`KSP.Sim.impl`);
  - `GameInstance.Parts : PartProvider`: `AllParts() : IReadOnlyCollection<PartCore>`, `TryGet(string, out PartCore)`, `GetRawJson(string)`, field `PartData : Dictionary<string, PartCore>`;
  - `PartCore.data : PartData` and `PartCore.modules : List<ModuleData>`;
  - `PartData` (class):
    - `partName`, `Title`, `category`, `family`, `cost` (int), `mass` (double, t);
    - `crewCapacity`, `maxTemp`, `crashTolerance`, `sizeCategory`;
    - `attachNodes : List<AttachNodeDefinition>` (`nodeID`);
    - `resourceContainers : List<ContainedResourceDefinition>` (`name`, `capacityUnits`);
  - `KSP.Modules.Data_Engine.engineModes : Data_Engine.EngineMode[]` and `currentEngineModeIndex`:
    - `EngineMode.maxThrust` (kN);
    - `EngineMode.atmosphereCurve : FloatCurve` (`Evaluate(float)`);
    - `EngineMode.propellant.mixtureName`;
  - `GameInstance.SessionManager`: `ActiveCampaignName`, `IsPartUnlocked(string)`, `GetMyAgencyUnlockedTechNodes() : HashSet<string>`;
  - `GameInstance.ScienceManager.TechNodeDataStore.PartIDLookup : IReadOnlyDictionary<string, string>` (part ID → tech node);
  - `GameInstance.CheatSystem.GetUnlockAllParts()`, `GameManager.Instance.GameModeManager.GetCurrentGameModeId()`;
  - `KSP.IO.IOProvider.ToJson(object)`, `FromJson<T>(string)`;
  - `SpaceWarp2.UI.API.Appbar.Appbar.RegisterOABAppButton(string, string, Texture2D, Action<bool>)`;
  - `UitkForKsp2.API.Window.Create(WindowOptions, VisualElement)` returns a `PanelRenderer`.
- **Not confirmable from the docs** (each one is logged or guarded; see the in-game checklist):
  - whether `IOProvider`'s default settings are the ones the game uses for `SerializedAssembly` and workspace files. A part-count check after every deserialization guards this;
  - whether `ApplySnapshot` after `ClearAll` is synchronous: the 3 s wait handles both cases. Whether `LoadSerializedAssembly` replaces or adds: method B deletes the old main first;
  - whether `atmosphereCurve` gives Isp in seconds: in KSP 1 it does, but the doc says "ISP multiplier". The dump says how the values were obtained;
  - whether `PartIDLookup` is keyed by `partName`;
  - whether `IObjectAssemblyResource.Name` is localized;
  - `UnityEngine.UIElements.VisualTreeAsset.stylesheets` is a Unity API, not a game one. If it does not compile, delete the loop in `VabToolsWindow.Build`: the inline fallback styles then apply.
- **Commits**: in English, imperative mood, ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
  - Stage only the files of the task, including the `.meta` files Unity generated for new files and folders.
  - The mod repo is `D:/KSPReduxModding/SkySpaceAgency` (remote `origin` = JLSkyzer/SkySpaceAgency). Push after each commit.
  - Never commit a game DLL (`Packages/KSP2_x64/*`).

### Commands used by several tasks

Run EditMode tests (compiles everything first):

```bash
S=/c/Users/killi/AppData/Local/Temp/claude/D--KSPReduxModding/a7cbf702-b607-4bb2-8603-776172a2b3f1/scratchpad
rm -f "$S/results.xml"
"/d/UnityHub/Editor/6000.6.0f1/Editor/Unity.exe" -batchmode -nographics -projectPath "D:/KSPReduxModding/SkySpaceAgency" -runTests -testPlatform EditMode -testResults "$S/results.xml" -logFile "$S/tests.log"
echo "exit=$?"
grep -c "error CS" "$S/tests.log"
grep -oE 'total="[0-9]+" passed="[0-9]+" failed="[0-9]+"' "$S/results.xml" | head -1
grep -oE 'name="[A-Za-z_]+"[^>]*result="Failed"' "$S/results.xml"
```

- `exit=0` and `failed="0"` mean all green.
- A compile error shows up as `error CS` lines, and no `results.xml` is written.
- All existing tests (Orbit, Landing, UI) must stay green. The total at the start of this plan is **131**. Each task below says how many tests it adds; the end total is **176**.
- Tasks that only touch game code have no unit test. For them, this same command is the compile check.

Build the release zip:

```bash
S=/c/Users/killi/AppData/Local/Temp/claude/D--KSPReduxModding/a7cbf702-b607-4bb2-8603-776172a2b3f1/scratchpad
P=D:/KSPReduxModding/SkySpaceAgency
rm -f "$P/Deploy/SkySpaceAgency.zip"
"/d/UnityHub/Editor/6000.6.0f1/Editor/Unity.exe" -batchmode -nographics -projectPath "$P" -executeMethod ThunderKit.Core.Pipelines.Pipeline.BatchModeExecutePipeline "-pipeline=Assets/SkySpaceAgency/Pipelines/Deploy to Zip File.asset" "-manifest=Assets/SkySpaceAgency/Pipelines/Deploy to Zip File Manifest.asset" -logFile "$S/deploy.log"
grep -c "error CS" "$S/deploy.log"
L=$(ls -t "$P/Assets/ThunderKitSettings/Logs/Deploy to Zip File/"*.asset | head -1); grep -c "logLevel: 2" "$L"
ls -la "$P/Deploy/SkySpaceAgency.zip"
```

This method **always exits with code 1**. Success means 0 `error CS`, 0 `logLevel: 2`, and the zip exists.

### Loader table (Task 8)

| File format | VAB has a main rocket | Methods, in order | Expected after loading |
|---|---|---|---|
| Workspace (`"Assemblies"`) | any | A with the file's snapshot | main part count of the file's `isMainAssembly` entry (or, without one, its total part count) |
| Export / raw assembly | yes | A (main swapped in the current snapshot), then B | main = imported part count |
| Export / raw assembly | no | B | main = imported part count |

After a failed method, the original snapshot is re-applied with A, and the restore is checked the same way before the next method.

---

## File Structure

| File | Responsibility |
|---|---|
| Create `Assets/Tests/Vab/SkySpaceAgency.Tests.Vab.asmdef` | Test assembly with Newtonsoft. |
| Create `Assets/SkySpaceAgency/Code/VabTools/VabJson.cs` | Lossless JSON parse and write; `Str`, `Num`, `GuidOf` readers. |
| Create `Assets/SkySpaceAgency/Code/VabTools/VabFiles.cs` | Folder names, safe file names, stamps, unique paths, import list. |
| Create `Assets/SkySpaceAgency/Code/VabTools/VabNum.cs` | Rounding to JSON (NaN → null), number text. |
| Create `Assets/SkySpaceAgency/Code/VabTools/VabSummary.cs` | DTOs `VabVessel`, `VabStage`, `VabPart`, `VabResource`; the export summary. |
| Create `Assets/SkySpaceAgency/Code/VabTools/VabExport.cs` | `VabCampaign`, `VabExportMeta`; the export document and its AI instructions. |
| Create `Assets/SkySpaceAgency/Code/VabTools/VabDump.cs` | `VabDumpPart`, `VabEngineInfo`; the dump document and its AI instructions. |
| Create `Assets/SkySpaceAgency/Code/VabTools/VabImport.cs` | Format detection, structure check, part names, missing parts, resource clamping. |
| Create `Assets/SkySpaceAgency/Code/VabTools/VabSnapshot.cs` | Main entry of a workspace snapshot, swap, part counts. |
| Create `Assets/SkySpaceAgency/Code/KSPService/VabReader.cs` | Builder → DTOs, game assembly JSON, all parts, campaign, capacities. |
| Create `Assets/SkySpaceAgency/Code/KSPService/VabLoader.cs` | Snapshot, deserialization with part-count check, methods A and B, settle check. |
| Create `Assets/SkySpaceAgency/Code/KSPService/VabActions.cs` | Export, dump, import pipeline, files, `[VabTools]` log. |
| Create `Assets/SkySpaceAgency/Code/UI/VabToolsWindow.cs` | Code-built window, VAB-only gating, AppBar sync. |
| Modify `Assets/SkySpaceAgency/Code/K2D2_Plugin.cs` | `VabFolder`, window creation, `RegisterOABAppButton`. |
| Create `Assets/Tests/Vab/*Tests.cs`, `Assets/Tests/Vab/VabFixtures.cs` | EditMode tests. |
| Modify `CHANGELOG.md`, `README.md` | Documentation. |

---

### Task 1: Test assembly, `VabJson` and `VabFiles`

**Files:**
- Create: `Assets/Tests/Vab/SkySpaceAgency.Tests.Vab.asmdef`
- Create: `Assets/SkySpaceAgency/Code/VabTools/VabJson.cs`, `Assets/SkySpaceAgency/Code/VabTools/VabFiles.cs`
- Test: `Assets/Tests/Vab/VabJsonTests.cs`, `Assets/Tests/Vab/VabFilesTests.cs`

**Interfaces:**
- Produces, in namespace `K2D2.VabTools`:
  - `VabJson.Parse(string) : JToken`: throws `JsonException` on broken JSON, empty text, `null` or text after the value. Date-like strings stay strings;
  - `VabJson.Write(JToken)` (indented) and `WriteCompact(JToken)`;
  - `VabJson.Str(JToken) : string` (null unless a JSON string), `Num(JToken) : double?` (numbers, or numeric strings such as `"-Infinity"`);
  - `VabJson.GuidOf(JToken idObject) : string`: the normalized `"D"` GUID of a `{"Guid": "..."}` object, or null. `VabJson.ZeroGuid`;
  - `VabFiles.ExportsFolder` / `ImportsFolder` / `DumpsFolder` (`"exports"`, `"imports"`, `"dumps"`), `MaxNameLength` (60);
  - `VabFiles.SafeName(string name, string fallback)`, `Stamp(DateTime)` (`yyyy-MM-dd_HHmmss`), `IsoTime(DateTime)`;
  - `VabFiles.ExportFileName(vessel, t)`, `BackupFileName(vessel, t)`, `DumpFileName(campaign, t)`;
  - `VabFiles.UniquePath(string folder, string fileName, Func<string, bool> exists)`;
  - `VabFiles.ImportChoices(IEnumerable<string> importFiles, IEnumerable<string> exportFiles) : List<string>`.

- [ ] **Step 1: Create the test assembly**

`Assets/Tests/Vab/SkySpaceAgency.Tests.Vab.asmdef`:

```json
{
  "name": "SkySpaceAgency.Tests.Vab",
  "references": [
    "SkySpaceAgency",
    "UnityEngine.TestRunner",
    "UnityEditor.TestRunner"
  ],
  "includePlatforms": [
    "Editor"
  ],
  "overrideReferences": true,
  "precompiledReferences": [
    "nunit.framework.dll",
    "Assembly-CSharp.dll",
    "Newtonsoft.Json.dll"
  ],
  "autoReferenced": false,
  "defineConstraints": [
    "UNITY_INCLUDE_TESTS"
  ]
}
```

It copies the three existing test asmdefs and adds `Newtonsoft.Json.dll`, the same precompiled name the mod's `SkySpaceAgency.asmdef` already lists. The DLL comes from `Library/PackageCache/com.unity.nuget.newtonsoft-json@*/Runtime/`, whose meta says `isExplicitlyReferenced: 0`. With `overrideReferences: true` it must still be named.

**How to verify:** if Step 2 shows `error CS0246: The type or namespace name 'Newtonsoft' could not be found` in a `Assets/Tests/Vab/` file (and not only "`VabJson` not found"), the reference did not resolve. In that case run `ls Library/PackageCache/ | grep newtonsoft`, check the DLL name, and fix the entry. Do not move on until the only errors are about the missing `VabJson` and `VabFiles` types.

- [ ] **Step 2: Write the failing tests**

`Assets/Tests/Vab/VabJsonTests.cs`:

```csharp
using K2D2.VabTools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class VabJsonTests
{
    [Test]
    public void Parse_KeepsDateLikeStringsAndBigIntegers()
    {
        // Values from a real workspace file: SaveTime is a 64-bit tick count, Temperature a string.
        JToken t = VabJson.Parse("{\"when\":\"2026-10-16T05:07:09\",\"SaveTime\":639277225550056177," +
                                 "\"Temperature\":\"-Infinity\",\"Mass\":288.014038}");
        Assert.AreEqual(JTokenType.String, t["when"].Type);
        Assert.AreEqual("2026-10-16T05:07:09", (string)t["when"]);
        Assert.AreEqual(639277225550056177L, (long)t["SaveTime"]);
        Assert.AreEqual("-Infinity", VabJson.Str(t["Temperature"]));
        Assert.AreEqual(288.014038, VabJson.Num(t["Mass"]).Value, 1e-12);
        Assert.AreEqual(double.NegativeInfinity, VabJson.Num(t["Temperature"]).Value);
        Assert.IsNull(VabJson.Num(t["missing"]));
        Assert.IsNull(VabJson.Str(t["Mass"]));
    }

    [Test]
    public void Parse_RejectsBrokenOrTrailingJson()
    {
        Assert.Catch<JsonException>(() => VabJson.Parse("{\"parts\": ["));
        Assert.Catch<JsonException>(() => VabJson.Parse(""));
        Assert.Catch<JsonException>(() => VabJson.Parse(null));
        Assert.Catch<JsonException>(() => VabJson.Parse("{} {}"));
    }

    [Test]
    public void Write_IsIndentedAndRoundTrips()
    {
        var doc = new JObject { ["a"] = 1, ["b"] = new JArray("x", 2.5) };
        string text = VabJson.Write(doc);
        StringAssert.Contains("\n", text);
        Assert.IsTrue(JToken.DeepEquals(doc, VabJson.Parse(text)));
        Assert.IsFalse(VabJson.WriteCompact(doc).Contains("\n"));
    }

    [Test]
    public void GuidOf_NormalizesOrReturnsNull()
    {
        Assert.AreEqual("8dda8ca2-e636-41fa-bb90-0ddf0c1c25dd",
            VabJson.GuidOf(new JObject { ["Guid"] = "8DDA8CA2-E636-41FA-BB90-0DDF0C1C25DD" }));
        Assert.IsNull(VabJson.GuidOf(new JObject { ["Guid"] = "not-a-guid" }));
        Assert.IsNull(VabJson.GuidOf(new JValue("8dda8ca2-e636-41fa-bb90-0ddf0c1c25dd"))); // not an object
        Assert.IsNull(VabJson.GuidOf(null));
    }
}
```

`Assets/Tests/Vab/VabFilesTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using K2D2.VabTools;
using NUnit.Framework;

public class VabFilesTests
{
    [Test]
    public void SafeName_ReplacesInvalidCharacters()
    {
        Assert.AreEqual("Bon vol_ Mk2_3_", VabFiles.SafeName("Bon vol: Mk2/3?", "vessel"));
        Assert.AreEqual("fusée pour orbite V2", VabFiles.SafeName("fusée pour orbite V2", "vessel"));
        Assert.AreEqual("a_b", VabFiles.SafeName("a\tb", "vessel"));
    }

    [Test]
    public void SafeName_FallsBackOnEmptyNames()
    {
        Assert.AreEqual("vessel", VabFiles.SafeName(null, "vessel"));
        Assert.AreEqual("vessel", VabFiles.SafeName("   ", "vessel"));
        Assert.AreEqual("vessel", VabFiles.SafeName("...", "vessel"));
        Assert.AreEqual("Rocket", VabFiles.SafeName("  Rocket. ", "vessel")); // Windows drops trailing dots
    }

    [Test]
    public void SafeName_TruncatesLongNames()
    {
        Assert.AreEqual(VabFiles.MaxNameLength, VabFiles.SafeName(new string('a', 100), "vessel").Length);
    }

    // Spec, "Fichiers": exports/<vessel>_<AAAA-MM-JJ_HHMMSS>.json, dumps/parts_<campaign>_<...>.json,
    // exports/before_import_<...>.json.
    [Test]
    public void FileNames_FollowTheSpec()
    {
        var t = new DateTime(2026, 10, 16, 5, 7, 9);
        Assert.AreEqual("Bon vol_2026-10-16_050709.json", VabFiles.ExportFileName("Bon vol", t));
        Assert.AreEqual("before_import_Bon vol_2026-10-16_050709.json", VabFiles.BackupFileName("Bon vol", t));
        Assert.AreEqual("parts_Skyzer Adventure_2026-10-16_050709.json", VabFiles.DumpFileName("Skyzer Adventure", t));
        Assert.AreEqual("vessel_2026-10-16_050709.json", VabFiles.ExportFileName("", t));
        Assert.AreEqual("parts_campaign_2026-10-16_050709.json", VabFiles.DumpFileName(null, t));
    }

    [Test]
    public void IsoTime_IsInvariant()
    {
        Assert.AreEqual("2026-10-16T05:07:09", VabFiles.IsoTime(new DateTime(2026, 10, 16, 5, 7, 9)));
        Assert.AreEqual("2026-10-16T05:07:09Z", VabFiles.IsoTime(new DateTime(2026, 10, 16, 5, 7, 9, DateTimeKind.Utc)));
    }

    [Test]
    public void UniquePath_AddsASuffixWhenTaken()
    {
        var taken = new HashSet<string> { Path.Combine("dir", "a.json"), Path.Combine("dir", "a_2.json") };
        Assert.AreEqual(Path.Combine("dir", "b.json"), VabFiles.UniquePath("dir", "b.json", taken.Contains));
        Assert.AreEqual(Path.Combine("dir", "a_3.json"), VabFiles.UniquePath("dir", "a.json", taken.Contains));
    }

    [Test]
    public void ImportChoices_ListsJsonFilesOfBothFolders()
    {
        List<string> choices = VabFiles.ImportChoices(
            new[] { "z.json", "notes.txt", "A.JSON" }, new[] { "before_import_x.json" });
        CollectionAssert.AreEqual(new[] { "imports/A.JSON", "imports/z.json", "exports/before_import_x.json" }, choices);
        CollectionAssert.IsEmpty(VabFiles.ImportChoices(null, null));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run the EditMode test command. Expected: `error CS0103` / `CS0246` about `VabJson` and `VabFiles` only (see Step 1 for a Newtonsoft error), and no `results.xml`.

- [ ] **Step 4: Implement**

`Assets/SkySpaceAgency/Code/VabTools/VabJson.cs`:

```csharp
using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    // JSON helpers of the VAB tools. Game JSON goes through them unchanged: no date parsing
    // (a date-like string stays a string), 64-bit integers kept, nothing after the value accepted.
    public static class VabJson
    {
        public const string ZeroGuid = "00000000-0000-0000-0000-000000000000";

        // Parses one JSON value. Throws JsonException on broken JSON, on empty or null text, and on
        // anything but comments after the value.
        public static JToken Parse(string text)
        {
            if (text == null)
                throw new JsonReaderException("No text.");
            using (var reader = new JsonTextReader(new StringReader(text)))
            {
                reader.DateParseHandling = DateParseHandling.None;
                reader.FloatParseHandling = FloatParseHandling.Double;
                JToken token = JToken.ReadFrom(reader);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonToken.Comment)
                        throw new JsonReaderException($"Unexpected content after the JSON value (line {reader.LineNumber}).");
                }
                return token;
            }
        }

        public static string Write(JToken token) => token.ToString(Formatting.Indented);

        public static string WriteCompact(JToken token) => token.ToString(Formatting.None);

        // The string, or null when the token is missing or not a JSON string.
        public static string Str(JToken token) =>
            token != null && token.Type == JTokenType.String ? (string)token : null;

        // A JSON number, or a string holding one ("-Infinity" in game files); null otherwise.
        public static double? Num(JToken token)
        {
            if (token == null)
                return null;
            switch (token.Type)
            {
                case JTokenType.Integer:
                case JTokenType.Float:
                    return token.Value<double>();
                case JTokenType.String:
                    return double.TryParse((string)token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                        ? v
                        : (double?)null;
                default:
                    return null;
            }
        }

        // Normalized GUID ("D" format, lower case) of a game id object {"Guid": "...", "DebugName": ...},
        // or null when it is missing, not an object or not a GUID.
        public static string GuidOf(JToken idObject)
        {
            string text = idObject is JObject o ? Str(o["Guid"]) : null;
            return text != null && Guid.TryParse(text, out Guid g) ? g.ToString("D") : null;
        }
    }
}
```

`Assets/SkySpaceAgency/Code/VabTools/VabFiles.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace K2D2.VabTools
{
    // File and folder names of the VAB tools, under <mod folder>/vab/ (spec, "Fichiers").
    public static class VabFiles
    {
        public const string ExportsFolder = "exports";
        public const string ImportsFolder = "imports";
        public const string DumpsFolder = "dumps";
        public const int MaxNameLength = 60;

        // Characters Windows refuses in a file name. Path.GetInvalidFileNameChars depends on the OS
        // the code runs on, so the list is fixed here.
        const string InvalidChars = "\"<>|:*?\\/";

        // name with invalid characters replaced by '_', at most MaxNameLength characters, without
        // trailing dots or spaces; fallback when nothing usable is left.
        public static string SafeName(string name, string fallback)
        {
            if (string.IsNullOrWhiteSpace(name))
                return fallback;
            var sb = new StringBuilder(name.Length);
            foreach (char c in name.Trim())
                sb.Append(char.IsControl(c) || InvalidChars.IndexOf(c) >= 0 ? '_' : c);
            string safe = sb.ToString();
            if (safe.Length > MaxNameLength)
                safe = safe.Substring(0, MaxNameLength);
            safe = safe.Trim().TrimEnd('.', ' ');
            return safe.Length == 0 ? fallback : safe;
        }

        public static string Stamp(DateTime time) =>
            time.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);

        public static string IsoTime(DateTime time) =>
            time.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);

        public static string ExportFileName(string vesselName, DateTime time) =>
            $"{SafeName(vesselName, "vessel")}_{Stamp(time)}.json";

        public static string BackupFileName(string vesselName, DateTime time) =>
            $"before_import_{SafeName(vesselName, "vessel")}_{Stamp(time)}.json";

        public static string DumpFileName(string campaignName, DateTime time) =>
            $"parts_{SafeName(campaignName, "campaign")}_{Stamp(time)}.json";

        // folder/fileName, or folder/<stem>_2.json, _3... when that path is taken (two exports
        // in the same second).
        public static string UniquePath(string folder, string fileName, Func<string, bool> exists)
        {
            string path = Path.Combine(folder, fileName);
            if (!exists(path))
                return path;
            string stem = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - 5)
                : fileName;
            for (int i = 2; ; i++)
            {
                path = Path.Combine(folder, $"{stem}_{i}.json");
                if (!exists(path))
                    return path;
            }
        }

        // Entries of the import list: "imports/<file>" then "exports/<file>", .json files only,
        // each folder sorted by name. An entry is also the path relative to the vab folder.
        public static List<string> ImportChoices(IEnumerable<string> importFiles, IEnumerable<string> exportFiles)
        {
            var choices = new List<string>();
            choices.AddRange(JsonFiles(importFiles).Select(f => ImportsFolder + "/" + f));
            choices.AddRange(JsonFiles(exportFiles).Select(f => ExportsFolder + "/" + f));
            return choices;
        }

        static IEnumerable<string> JsonFiles(IEnumerable<string> files) =>
            (files ?? Enumerable.Empty<string>())
                .Where(f => !string.IsNullOrEmpty(f) && f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run the EditMode test command. Expected: `exit=0` and `failed="0"`, with 11 more tests than before (total 142).

- [ ] **Step 6: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/Tests/Vab.meta Assets/Tests/Vab/SkySpaceAgency.Tests.Vab.asmdef* Assets/Tests/Vab/VabJsonTests.cs* Assets/Tests/Vab/VabFilesTests.cs* Assets/SkySpaceAgency/Code/VabTools.meta Assets/SkySpaceAgency/Code/VabTools/VabJson.cs* Assets/SkySpaceAgency/Code/VabTools/VabFiles.cs*
git status --short   # only these paths
git commit -m "Add VAB tools JSON and file name helpers with their test assembly

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 2: `VabNum` and `VabSummary`

**Files:**
- Create: `Assets/SkySpaceAgency/Code/VabTools/VabNum.cs`, `Assets/SkySpaceAgency/Code/VabTools/VabSummary.cs`
- Test: `Assets/Tests/Vab/VabSummaryTests.cs`

**Interfaces:**
- Produces:
  - `VabNum.Round(double, int digits) : JToken` (JSON null for NaN or infinity), `Finite(double)`, `Text(double)` (`0.###`, invariant), `Json(string) : JToken` (JSON null for null);
  - the DTOs `VabResource { Name, Amount, Capacity, MassT }`;
  - `VabPart { Guid, Name, Title, ParentGuid, AttachNode, SymmetryGroup (int?), DryMassT, Cost, Resources, Modules }`;
  - `VabStage { Stage, PartGuids, EngineGuids, StartMassT, EndMassT, DryMassT, FuelMassT, IspVac, IspAsl, ThrustVacKN, ThrustAslKN, TwrVac, TwrAsl, TwrActual, DeltaVVac, DeltaVAsl, DeltaVActual, BurnTimeS, SituationBody, SituationAtmosphere, SituationAltitudeM }`;
  - `VabVessel { Name, MassWetT, MassDryT, DeltaVTotalVac, DeltaVTotalActual, DeltaVReady, Stages, Parts }`;
  - the digit constants `VabSummary.MassDigits` … `CostDigits`;
  - `VabSummary.Build(VabVessel) : JObject`, the spec's `summary` object.

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/Vab/VabSummaryTests.cs`:

```csharp
using K2D2.VabTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class VabSummaryTests
{
    // Pod (root) <- tank <- engine, two radial tanks in one symmetry set, an antenna in no stage.
    static VabVessel Sample()
    {
        var v = new VabVessel
        {
            Name = "Bon vol", MassWetT = 12.34567, MassDryT = 4.5,
            DeltaVTotalVac = 3456.78, DeltaVTotalActual = 3012.34, DeltaVReady = true,
        };
        v.Parts.Add(new VabPart
        {
            Guid = "a", Name = "pod_2v_lander_crew", Title = "Lander pod", DryMassT = 0.8, Cost = 1200,
            Resources = { new VabResource { Name = "Monopropellant", Amount = 10, Capacity = 10, MassT = 0.04 } },
            Modules = { "Data_Command" },
        });
        v.Parts.Add(new VabPart
        {
            Guid = "b", Name = "fueltank_2v_inline_methalox_1x2", Title = "Tank", ParentGuid = "a", AttachNode = "top",
            DryMassT = 0.25, Cost = 300,
            Resources =
            {
                new VabResource { Name = "Methane", Amount = 1.6, Capacity = 1.6, MassT = 0.5 },
                new VabResource { Name = "Oxidizer", Amount = 6.4, Capacity = 6.4, MassT = 2.0 },
            },
        });
        v.Parts.Add(new VabPart
        {
            Guid = "c", Name = "engine_2v_methalox_poodle", Title = "Engine", ParentGuid = "b", AttachNode = "top",
            DryMassT = 1.0, Cost = 1500, Modules = { "Data_Engine", "Data_Gimbal" },
        });
        for (int i = 0; i < 2; i++)
            v.Parts.Add(new VabPart
            {
                Guid = i == 0 ? "d" : "e", Name = "fueltank_0v_radial_methalox", Title = "Radial tank",
                ParentGuid = "b", AttachNode = "srfAttach", SymmetryGroup = 7, DryMassT = 0.05,
                Resources = { new VabResource { Name = "Methane", Amount = 0.5, Capacity = 1.0, MassT = 0.1 } },
            });
        v.Parts.Add(new VabPart { Guid = "f", Name = "antenna_0v_16", Title = "Antenna", ParentGuid = "a", AttachNode = "srfAttach", DryMassT = 0.01 });

        v.Stages.Add(new VabStage
        {
            Stage = 1, PartGuids = { "c", "b", "d", "e", "zz" }, EngineGuids = { "c" },
            StartMassT = 12.34567, EndMassT = 6.1, DryMassT = 1.35, FuelMassT = 6.2,
            IspVac = 350.04, IspAsl = 90.06, ThrustVacKN = 60.04, ThrustAslKN = 15.46,
            TwrVac = 1.23456, TwrAsl = 0.314, TwrActual = 1.2,
            DeltaVVac = 1234.567, DeltaVAsl = 300.04, DeltaVActual = 1200.06, BurnTimeS = 95.56,
            SituationBody = "Kerbin", SituationAtmosphere = false, SituationAltitudeM = 0,
        });
        v.Stages.Add(new VabStage
        {
            Stage = 0, PartGuids = { "a" }, DeltaVVac = double.NaN, IspVac = double.PositiveInfinity,
            SituationBody = "Mun", SituationAtmosphere = false, SituationAltitudeM = 1500.4,
        });
        return v;
    }

    static JObject PartByGuid(JObject summary, string guid)
    {
        foreach (JToken p in (JArray)summary["parts"])
            if ((string)p["guid"] == guid)
                return (JObject)p;
        return null;
    }

    [Test]
    public void Build_CopiesVesselTotals()
    {
        JObject s = VabSummary.Build(Sample());
        Assert.AreEqual("Bon vol", (string)s["vessel_name"]);
        Assert.AreEqual(6, (int)s["part_count"]);
        Assert.AreEqual(12.346, (double)s["mass_wet_t"], 1e-9);
        Assert.AreEqual(4.5, (double)s["mass_dry_t"], 1e-9);
        Assert.AreEqual(3456.8, (double)s["delta_v_total_vac"], 1e-9);
        Assert.AreEqual(3012.3, (double)s["delta_v_total_actual"], 1e-9);
        Assert.IsTrue((bool)s["delta_v_ready"]);
    }

    [Test]
    public void Build_AggregatesResourcesByName()
    {
        var totals = (JArray)VabSummary.Build(Sample())["resources_total"];
        Assert.AreEqual(3, totals.Count);
        Assert.AreEqual("Monopropellant", (string)totals[0]["name"]);
        Assert.AreEqual(10.0, (double)totals[0]["amount"], 1e-9);
        Assert.AreEqual("Methane", (string)totals[1]["name"]);
        Assert.AreEqual(2.6, (double)totals[1]["amount"], 1e-9);   // 1.6 + 0.5 + 0.5
        Assert.AreEqual(3.6, (double)totals[1]["capacity"], 1e-9); // 1.6 + 1 + 1
        Assert.AreEqual(0.7, (double)totals[1]["mass_t"], 1e-9);
        Assert.AreEqual("Oxidizer", (string)totals[2]["name"]);
        Assert.AreEqual(2.0, (double)totals[2]["mass_t"], 1e-9);
    }

    [Test]
    public void Build_StagesUseTitlesInGameOrder()
    {
        var stages = (JArray)VabSummary.Build(Sample())["stages"];
        Assert.AreEqual(2, stages.Count);
        JToken s1 = stages[0];
        Assert.AreEqual(1, (int)s1["stage"]);
        CollectionAssert.AreEqual(new[] { "Engine", "Tank", "Radial tank", "Radial tank", "zz" },
            ((JArray)s1["parts"]).ToObject<string[]>());                 // an unknown GUID stays as is
        CollectionAssert.AreEqual(new[] { "Engine" }, ((JArray)s1["engines_active"]).ToObject<string[]>());
        Assert.AreEqual(350.0, (double)s1["isp_vac"], 1e-9);
        Assert.AreEqual(90.1, (double)s1["isp_asl"], 1e-9);
        Assert.AreEqual(15.5, (double)s1["thrust_asl_kN"], 1e-9);
        Assert.AreEqual(1.23, (double)s1["twr_vac"], 1e-9);
        Assert.AreEqual(1234.6, (double)s1["delta_v_vac"], 1e-9);
        Assert.AreEqual(1200.1, (double)s1["delta_v_actual"], 1e-9);
        Assert.AreEqual(95.6, (double)s1["burn_time_s"], 1e-9);
        Assert.AreEqual(12.346, (double)s1["start_mass_t"], 1e-9);
        Assert.AreEqual("Kerbin", (string)s1["situation"]["body"]);
        Assert.IsFalse((bool)s1["situation"]["atmosphere"]);
        Assert.AreEqual(0, (int)stages[1]["stage"]);
        CollectionAssert.AreEqual(new[] { "Lander pod" }, ((JArray)stages[1]["parts"]).ToObject<string[]>());
        CollectionAssert.IsEmpty((JArray)stages[1]["engines_active"]);
        Assert.AreEqual(1500.0, (double)stages[1]["situation"]["altitude_m"], 1e-9);
    }

    [Test]
    public void Build_NotANumberBecomesNull()
    {
        JObject s = VabSummary.Build(Sample());
        Assert.AreEqual(JTokenType.Null, s["stages"][1]["delta_v_vac"].Type);
        Assert.AreEqual(JTokenType.Null, s["stages"][1]["isp_vac"].Type);
        string text = VabJson.Write(s);
        StringAssert.DoesNotContain("NaN", text);
        StringAssert.DoesNotContain("Infinity", text);
    }

    [Test]
    public void Build_PartsCarryLinksStagesAndMass()
    {
        JObject s = VabSummary.Build(Sample());
        JObject pod = PartByGuid(s, "a");
        Assert.AreEqual(JTokenType.Null, pod["parent_guid"].Type);
        Assert.AreEqual(JTokenType.Null, pod["attach_node"].Type);
        Assert.AreEqual(0, (int)pod["stage_index"]);
        Assert.AreEqual(JTokenType.Null, pod["symmetry_group"].Type);
        Assert.AreEqual(0.84, (double)pod["mass_t"], 1e-9);      // 0.8 dry + 0.04 monopropellant
        Assert.AreEqual(0.8, (double)pod["dry_mass_t"], 1e-9);
        Assert.AreEqual(1200.0, (double)pod["cost"], 1e-9);
        CollectionAssert.AreEqual(new[] { "Data_Command" }, ((JArray)pod["modules"]).ToObject<string[]>());
        Assert.AreEqual("Monopropellant", (string)pod["resources"][0]["name"]);
        Assert.AreEqual(10.0, (double)pod["resources"][0]["amount"], 1e-9);

        JObject tank = PartByGuid(s, "b");
        Assert.AreEqual("a", (string)tank["parent_guid"]);
        Assert.AreEqual("top", (string)tank["attach_node"]);
        Assert.AreEqual(1, (int)tank["stage_index"]);
        Assert.AreEqual(2.75, (double)tank["mass_t"], 1e-9);     // 0.25 + 0.5 + 2.0

        Assert.AreEqual(7, (int)PartByGuid(s, "d")["symmetry_group"]);
        Assert.AreEqual(JTokenType.Null, PartByGuid(s, "f")["stage_index"].Type);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run the EditMode test command. Expected: `error CS0246` about `VabVessel`, `VabPart`, `VabStage`, `VabResource` and `VabSummary`, and no `results.xml`.

- [ ] **Step 3: Implement**

`Assets/SkySpaceAgency/Code/VabTools/VabNum.cs`:

```csharp
using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    public static class VabNum
    {
        // value rounded to digits decimals, or JSON null for NaN and infinities (JSON has no NaN,
        // and Newtonsoft would write a bare NaN token that most parsers reject).
        public static JToken Round(double value, int digits)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return JValue.CreateNull();
            return new JValue(Math.Round(value, digits, MidpointRounding.AwayFromZero));
        }

        // 0 for NaN and infinities, for sums.
        public static double Finite(double value) =>
            double.IsNaN(value) || double.IsInfinity(value) ? 0 : value;

        // Short invariant text for messages: 5, 1.6, -1.
        public static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        // A JSON string, or JSON null (not a null-valued string token) for null.
        public static JToken Json(string value) => value == null ? JValue.CreateNull() : new JValue(value);
    }
}
```

`Assets/SkySpaceAgency/Code/VabTools/VabSummary.cs`:

```csharp
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    public class VabResource
    {
        public string Name;
        public double Amount;
        public double Capacity;
        public double MassT;
    }

    public class VabPart
    {
        public string Guid;
        public string Name;          // internal part name (partName)
        public string Title;         // display title
        public string ParentGuid;    // null for the root part
        public string AttachNode;    // this part's node connected to its parent; null for the root
        public int? SymmetryGroup;   // same number = same symmetry set
        public double DryMassT;
        public double Cost;
        public List<VabResource> Resources = new();
        public List<string> Modules = new();
    }

    public class VabStage
    {
        public int Stage;                       // the game's stage number
        public List<string> PartGuids = new();  // parts of this stage
        public List<string> EngineGuids = new(); // engines burning in this stage
        public double StartMassT, EndMassT, DryMassT, FuelMassT;
        public double IspVac, IspAsl, ThrustVacKN, ThrustAslKN;
        public double TwrVac, TwrAsl, TwrActual;
        public double DeltaVVac, DeltaVAsl, DeltaVActual, BurnTimeS;
        public string SituationBody;
        public bool SituationAtmosphere;
        public double SituationAltitudeM;
    }

    public class VabVessel
    {
        public string Name;
        public double MassWetT, MassDryT;
        public double DeltaVTotalVac, DeltaVTotalActual;
        public bool DeltaVReady;
        public List<VabStage> Stages = new();  // in the game's order
        public List<VabPart> Parts = new();
    }

    // The "summary" section of an export (spec, "Contenu de l'export").
    public static class VabSummary
    {
        public const int MassDigits = 3;
        public const int DeltaVDigits = 1;
        public const int IspDigits = 1;
        public const int ThrustDigits = 1;
        public const int TwrDigits = 2;
        public const int TimeDigits = 1;
        public const int AltitudeDigits = 0;
        public const int AmountDigits = 3;
        public const int CostDigits = 0;

        public static JObject Build(VabVessel vessel)
        {
            var titles = new Dictionary<string, string>();
            foreach (VabPart p in vessel.Parts)
                if (p.Guid != null && !titles.ContainsKey(p.Guid))
                    titles[p.Guid] = DisplayName(p);

            // A part's stage: the first stage that lists it.
            var stageOf = new Dictionary<string, int>();
            foreach (VabStage s in vessel.Stages)
                foreach (string g in s.PartGuids)
                    if (g != null && !stageOf.ContainsKey(g))
                        stageOf[g] = s.Stage;

            var stages = new JArray();
            foreach (VabStage s in vessel.Stages)
                stages.Add(StageJson(s, titles));

            var parts = new JArray();
            foreach (VabPart p in vessel.Parts)
                parts.Add(PartJson(p, stageOf));

            return new JObject
            {
                ["vessel_name"] = VabNum.Json(vessel.Name),
                ["part_count"] = vessel.Parts.Count,
                ["mass_wet_t"] = VabNum.Round(vessel.MassWetT, MassDigits),
                ["mass_dry_t"] = VabNum.Round(vessel.MassDryT, MassDigits),
                ["delta_v_total_vac"] = VabNum.Round(vessel.DeltaVTotalVac, DeltaVDigits),
                ["delta_v_total_actual"] = VabNum.Round(vessel.DeltaVTotalActual, DeltaVDigits),
                ["delta_v_ready"] = vessel.DeltaVReady,
                ["stages"] = stages,
                ["parts"] = parts,
                ["resources_total"] = Totals(vessel.Parts),
            };
        }

        static string DisplayName(VabPart p) =>
            !string.IsNullOrEmpty(p.Title) ? p.Title : !string.IsNullOrEmpty(p.Name) ? p.Name : p.Guid;

        static JArray Titles(List<string> guids, Dictionary<string, string> titles)
        {
            var list = new JArray();
            foreach (string g in guids)
                if (g != null)
                    list.Add(titles.TryGetValue(g, out string title) ? title : g);
            return list;
        }

        static JObject StageJson(VabStage s, Dictionary<string, string> titles) => new JObject
        {
            ["stage"] = s.Stage,
            ["parts"] = Titles(s.PartGuids, titles),
            ["engines_active"] = Titles(s.EngineGuids, titles),
            ["start_mass_t"] = VabNum.Round(s.StartMassT, MassDigits),
            ["end_mass_t"] = VabNum.Round(s.EndMassT, MassDigits),
            ["dry_mass_t"] = VabNum.Round(s.DryMassT, MassDigits),
            ["fuel_mass_t"] = VabNum.Round(s.FuelMassT, MassDigits),
            ["isp_vac"] = VabNum.Round(s.IspVac, IspDigits),
            ["isp_asl"] = VabNum.Round(s.IspAsl, IspDigits),
            ["thrust_vac_kN"] = VabNum.Round(s.ThrustVacKN, ThrustDigits),
            ["thrust_asl_kN"] = VabNum.Round(s.ThrustAslKN, ThrustDigits),
            ["twr_vac"] = VabNum.Round(s.TwrVac, TwrDigits),
            ["twr_asl"] = VabNum.Round(s.TwrAsl, TwrDigits),
            ["twr_actual"] = VabNum.Round(s.TwrActual, TwrDigits),
            ["delta_v_vac"] = VabNum.Round(s.DeltaVVac, DeltaVDigits),
            ["delta_v_asl"] = VabNum.Round(s.DeltaVAsl, DeltaVDigits),
            ["delta_v_actual"] = VabNum.Round(s.DeltaVActual, DeltaVDigits),
            ["burn_time_s"] = VabNum.Round(s.BurnTimeS, TimeDigits),
            ["situation"] = new JObject
            {
                ["body"] = VabNum.Json(s.SituationBody),
                ["atmosphere"] = s.SituationAtmosphere,
                ["altitude_m"] = VabNum.Round(s.SituationAltitudeM, AltitudeDigits),
            },
        };

        static JObject ResourceJson(VabResource r) => new JObject
        {
            ["name"] = VabNum.Json(r.Name),
            ["amount"] = VabNum.Round(r.Amount, AmountDigits),
            ["capacity"] = VabNum.Round(r.Capacity, AmountDigits),
            ["mass_t"] = VabNum.Round(r.MassT, MassDigits),
        };

        static JObject PartJson(VabPart p, Dictionary<string, int> stageOf)
        {
            var resources = new JArray();
            double resourceMass = 0;
            foreach (VabResource r in p.Resources)
            {
                resources.Add(ResourceJson(r));
                resourceMass += VabNum.Finite(r.MassT);
            }
            return new JObject
            {
                ["guid"] = VabNum.Json(p.Guid),
                ["name"] = VabNum.Json(p.Name),
                ["title"] = VabNum.Json(p.Title),
                ["parent_guid"] = VabNum.Json(p.ParentGuid),
                ["attach_node"] = VabNum.Json(p.AttachNode),
                ["stage_index"] = p.Guid != null && stageOf.TryGetValue(p.Guid, out int stage)
                    ? new JValue(stage)
                    : JValue.CreateNull(),
                ["symmetry_group"] = p.SymmetryGroup.HasValue ? new JValue(p.SymmetryGroup.Value) : JValue.CreateNull(),
                ["mass_t"] = VabNum.Round(VabNum.Finite(p.DryMassT) + resourceMass, MassDigits),
                ["dry_mass_t"] = VabNum.Round(p.DryMassT, MassDigits),
                ["cost"] = VabNum.Round(p.Cost, CostDigits),
                ["resources"] = resources,
                ["modules"] = new JArray(p.Modules),
            };
        }

        static JArray Totals(List<VabPart> parts)
        {
            var order = new List<VabResource>();
            var byName = new Dictionary<string, VabResource>();
            foreach (VabPart p in parts)
                foreach (VabResource r in p.Resources)
                {
                    string key = r.Name ?? "?";
                    if (!byName.TryGetValue(key, out VabResource total))
                    {
                        total = new VabResource { Name = key };
                        byName[key] = total;
                        order.Add(total);
                    }
                    total.Amount += VabNum.Finite(r.Amount);
                    total.Capacity += VabNum.Finite(r.Capacity);
                    total.MassT += VabNum.Finite(r.MassT);
                }
            var list = new JArray();
            foreach (VabResource t in order)
                list.Add(ResourceJson(t));
            return list;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the EditMode test command. Expected: `exit=0` and `failed="0"`, with 5 more tests (total 147).

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/VabTools/VabNum.cs* Assets/SkySpaceAgency/Code/VabTools/VabSummary.cs* Assets/Tests/Vab/VabSummaryTests.cs*
git status --short   # only these paths
git commit -m "Add the VAB export summary: stages, parts and resource totals

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 3: `VabExport`

**Files:**
- Create: `Assets/SkySpaceAgency/Code/VabTools/VabExport.cs`
- Test: `Assets/Tests/Vab/VabExportTests.cs`

**Interfaces:**
- Produces:
  - `VabCampaign { Name, Mode, UnlockAllParts, UnlockedTechNodes }` with `ToJson(bool withTechNodes) : JObject`;
  - `VabExportMeta { ExportedAt, GameVersion, ModVersion, Campaign }`;
  - `VabExport.Format` (`"SkySpaceAgency.VabExport"`), `Version` (1), `InstructionsForAi`;
  - `VabExport.Build(VabExportMeta, JObject summary, JToken gameAssembly) : JObject`, with keys in the spec's order. Both sections are deep copies.

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/Vab/VabExportTests.cs`:

```csharp
using System;
using System.Linq;
using K2D2.VabTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class VabExportTests
{
    static VabExportMeta Meta() => new VabExportMeta
    {
        ExportedAt = new DateTime(2026, 10, 16, 5, 7, 9),
        GameVersion = "0.2.9.0.104880",
        ModVersion = "1.3.0",
        Campaign = new VabCampaign { Name = "Skyzer Adventure", Mode = "exploration", UnlockAllParts = false },
    };

    [Test]
    public void Build_HasFormatVersionAndMetadata()
    {
        JObject doc = VabExport.Build(Meta(), new JObject { ["part_count"] = 3 }, new JObject { ["parts"] = new JArray() });
        CollectionAssert.AreEqual(
            new[] { "format", "version", "exported_at", "game_version", "mod_version", "campaign",
                    "instructions_for_ai", "summary", "game_assembly" },
            doc.Properties().Select(p => p.Name).ToArray());
        Assert.AreEqual("SkySpaceAgency.VabExport", (string)doc["format"]);
        Assert.AreEqual(1, (int)doc["version"]);
        Assert.AreEqual("2026-10-16T05:07:09", (string)doc["exported_at"]);
        Assert.AreEqual("0.2.9.0.104880", (string)doc["game_version"]);
        Assert.AreEqual("1.3.0", (string)doc["mod_version"]);
        Assert.AreEqual("Skyzer Adventure", (string)doc["campaign"]["name"]);
        Assert.AreEqual("exploration", (string)doc["campaign"]["mode"]);
        Assert.IsFalse((bool)doc["campaign"]["unlock_all_parts"]);
        Assert.IsNull(doc["campaign"]["unlocked_tech_nodes"]); // only in dumps
        Assert.AreEqual(3, (int)doc["summary"]["part_count"]);
    }

    [Test]
    public void Build_EmbedsTheGameAssemblyAsACopy()
    {
        var assembly = new JObject { ["parts"] = new JArray(new JObject { ["partName"] = "pod_2v_lander_crew" }) };
        JObject doc = VabExport.Build(Meta(), new JObject(), assembly);
        Assert.IsTrue(JToken.DeepEquals(assembly, doc["game_assembly"]));
        assembly["parts"][0]["partName"] = "changed";
        Assert.AreEqual("pod_2v_lander_crew", (string)doc["game_assembly"]["parts"][0]["partName"]);
        Assert.AreEqual(JTokenType.Null, VabExport.Build(Meta(), null, null)["game_assembly"].Type);
    }

    // Spec: the instructions say what each section is, that game_assembly is what the import reads,
    // to keep GUIDs and node pairs consistent, and to use only parts marked unlocked in a dump.
    [Test]
    public void Build_InstructionsStateTheImportRules()
    {
        string text = (string)VabExport.Build(Meta(), new JObject(), new JObject())["instructions_for_ai"];
        StringAssert.Contains("summary", text);
        StringAssert.Contains("game_assembly", text);
        StringAssert.Contains("PartGuid", text);
        StringAssert.Contains("attachNodeStates", text);
        StringAssert.Contains("RootGuid", text);
        StringAssert.Contains("\"unlocked\": true", text);
        StringAssert.Contains("capacity", text);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run the EditMode test command. Expected: `error CS0246` about `VabExportMeta`, `VabCampaign` and `VabExport`.

- [ ] **Step 3: Implement**

`Assets/SkySpaceAgency/Code/VabTools/VabExport.cs`:

```csharp
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    public class VabCampaign
    {
        public string Name;
        public string Mode;
        public bool UnlockAllParts;
        public List<string> UnlockedTechNodes = new();

        public JObject ToJson(bool withTechNodes)
        {
            var o = new JObject
            {
                ["name"] = VabNum.Json(Name),
                ["mode"] = VabNum.Json(Mode),
                ["unlock_all_parts"] = UnlockAllParts,
            };
            if (withTechNodes)
                o["unlocked_tech_nodes"] = new JArray(UnlockedTechNodes ?? new List<string>());
            return o;
        }
    }

    public class VabExportMeta
    {
        public DateTime ExportedAt;
        public string GameVersion;
        public string ModVersion;
        public VabCampaign Campaign = new();
    }

    // The export document (spec, "Contenu de l'export").
    public static class VabExport
    {
        public const string Format = "SkySpaceAgency.VabExport";
        public const int Version = 1;

        public const string InstructionsForAi =
            "Exported by the SkySpaceAgency mod from the KSP2 Redux VAB. " +
            "'summary' is a readable digest computed by the game's VAB: per stage, in the game's stage order, " +
            "the parts, the active engines, masses (t), Isp (s), thrust (kN), TWR, delta-v (m/s) in vacuum, at sea " +
            "level and in the stage's situation ('actual'), and burn time (s); then every part (GUID, name, title, " +
            "parent, attach node, stage, symmetry group, mass, cost, resources, modules) and the resource totals. " +
            "'game_assembly' is the game's own JSON for this rocket (a SerializedAssembly): it is the only section " +
            "the mod's Import reads back, the summary is ignored on import. To hand back a corrected rocket, edit " +
            "'game_assembly' and send back either this whole file or that object alone. Keep every part's PartGuid " +
            "unique; keep AssemblyOABConfig.RootGuid on an existing part; keep node pairs consistent: when part A's " +
            "attachNodeStates entry points to part B (AttachedPartGuid and attachedPartNodeID), part B must have the " +
            "matching entry pointing back to A; when you add or remove a part, update the stages (PartIds in " +
            "stagingState and AssemblyOABConfig.StagingState) and SymmetrySets too. Use only part names marked " +
            "\"unlocked\": true in a SkySpaceAgency parts dump of the same campaign: the import refuses any other " +
            "part. Resource amounts above a part's capacity are lowered to the capacity on import.";

        public static JObject Build(VabExportMeta meta, JObject summary, JToken gameAssembly)
        {
            return new JObject
            {
                ["format"] = Format,
                ["version"] = Version,
                ["exported_at"] = VabFiles.IsoTime(meta.ExportedAt),
                ["game_version"] = VabNum.Json(meta.GameVersion),
                ["mod_version"] = VabNum.Json(meta.ModVersion),
                ["campaign"] = (meta.Campaign ?? new VabCampaign()).ToJson(false),
                ["instructions_for_ai"] = InstructionsForAi,
                ["summary"] = summary == null ? new JObject() : summary.DeepClone(),
                ["game_assembly"] = gameAssembly == null ? JValue.CreateNull() : gameAssembly.DeepClone(),
            };
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the EditMode test command. Expected: `exit=0` and `failed="0"`, with 3 more tests (total 150).

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/VabTools/VabExport.cs* Assets/Tests/Vab/VabExportTests.cs*
git status --short   # only these paths
git commit -m "Add the VAB export document with its instructions for an AI

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 4: `VabDump`

**Files:**
- Create: `Assets/SkySpaceAgency/Code/VabTools/VabDump.cs`
- Test: `Assets/Tests/Vab/VabDumpTests.cs`

**Interfaces:**
- Produces:
  - `VabEngineInfo { ThrustVacKN, ThrustAslKN, IspVac, IspAsl, Propellants }`;
  - `VabDumpPart { Name, Title, Category, Family, TechNode, Size, Unlocked, CampaignUnlocked (bool?), Cost, MassT, MaxTemp, CrashTolerance, CrewCapacity, AttachNodes, Resources (Name and Capacity used), Engine, Modules, RawJson }`;
  - `VabDump.Format` (`"SkySpaceAgency.PartsDump"`), `Version` (1), `Instructions`, `NoExampleNote`, `InstructionsFor(bool hasExample)`;
  - `VabDump.Build(VabCampaign, DateTime exportedAt, string gameVersion, string modVersion, IEnumerable<VabDumpPart>, JToken exampleAssembly, bool templatesUnlockedOnly) : JObject`.

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/Vab/VabDumpTests.cs`:

```csharp
using System;
using System.Linq;
using K2D2.VabTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

public class VabDumpTests
{
    static readonly DateTime At = new DateTime(2026, 10, 16, 5, 7, 9);

    static VabDumpPart Engine() => new VabDumpPart
    {
        Name = "engine_2v_methalox_poodle", Title = "Poodle", Category = "Engine", Family = "methalox",
        TechNode = "tNode_2v_methalox", Unlocked = false, Cost = 1500, MassT = 0.9, Size = "Medium",
        CrewCapacity = 0, MaxTemp = 2000, CrashTolerance = 8, AttachNodes = { "top", "bottom" },
        Engine = new VabEngineInfo
        {
            ThrustVacKN = 60.04, ThrustAslKN = double.NaN, IspVac = 350.04, IspAsl = 90.06, Propellants = { "Methalox" },
        },
        Modules = { "Data_Engine" },
        RawJson = "{\"data\":{\"partName\":\"engine_2v_methalox_poodle\"}}",
    };

    static VabDumpPart Tank(string name, bool unlocked) => new VabDumpPart
    {
        Name = name, Title = "Tank " + name, Category = "FuelTank", Unlocked = unlocked, CampaignUnlocked = unlocked,
        MassT = 0.25, Resources = { new VabResource { Name = "Methane", Capacity = 1.6 } },
        RawJson = "{\"data\":{\"partName\":\"" + name + "\"}}",
    };

    static JObject Build(VabDumpPart[] parts, JToken example = null, bool unlockedOnly = false,
                         VabCampaign campaign = null) =>
        VabDump.Build(campaign ?? new VabCampaign { Name = "Skyzer Adventure" }, At, "0.2.9", "1.3.0",
                      parts, example, unlockedOnly);

    static JObject Part(JObject dump, string name) =>
        (JObject)((JArray)dump["parts"]).First(p => (string)p["name"] == name);

    [Test]
    public void Build_SortsPartsAndCountsUnlocked()
    {
        JObject dump = Build(new[] { Tank("zeta", true), Tank("alpha", false), Tank("mid", true) });
        Assert.AreEqual("SkySpaceAgency.PartsDump", (string)dump["format"]);
        Assert.AreEqual(1, (int)dump["version"]);
        Assert.AreEqual("2026-10-16T05:07:09", (string)dump["exported_at"]);
        CollectionAssert.AreEqual(new[] { "alpha", "mid", "zeta" },
            ((JArray)dump["parts"]).Select(p => (string)p["name"]).ToArray());
        Assert.AreEqual(3, (int)dump["part_count"]);
        Assert.AreEqual(2, (int)dump["unlocked_count"]);
        Assert.IsFalse((bool)Part(dump, "alpha")["unlocked"]);
        Assert.IsTrue((bool)Part(dump, "zeta")["unlocked"]);
    }

    [Test]
    public void Build_WritesPartFields()
    {
        JObject dump = Build(new[] { Engine(), Tank("tank", true) });
        JObject engine = Part(dump, "engine_2v_methalox_poodle");
        Assert.AreEqual("Poodle", (string)engine["title"]);
        Assert.AreEqual("Engine", (string)engine["category"]);
        Assert.AreEqual("methalox", (string)engine["family"]);
        Assert.AreEqual("tNode_2v_methalox", (string)engine["tech_node"]);
        Assert.IsFalse((bool)engine["unlocked"]);
        Assert.AreEqual(JTokenType.Null, engine["campaign_unlocked"].Type);
        Assert.AreEqual(1500.0, (double)engine["cost"], 1e-9);
        Assert.AreEqual(0.9, (double)engine["mass_t"], 1e-9);
        Assert.AreEqual("Medium", (string)engine["size"]);
        Assert.AreEqual(2000.0, (double)engine["max_temp"], 1e-9);
        CollectionAssert.AreEqual(new[] { "top", "bottom" }, ((JArray)engine["attach_nodes"]).ToObject<string[]>());
        Assert.AreEqual(60.0, (double)engine["engine"]["thrust_vac_kN"], 1e-9);
        Assert.AreEqual(JTokenType.Null, engine["engine"]["thrust_asl_kN"].Type);
        Assert.AreEqual(350.0, (double)engine["engine"]["isp_vac"], 1e-9);
        Assert.AreEqual(90.1, (double)engine["engine"]["isp_asl"], 1e-9);
        CollectionAssert.AreEqual(new[] { "Methalox" }, ((JArray)engine["engine"]["propellants"]).ToObject<string[]>());
        CollectionAssert.AreEqual(new[] { "Data_Engine" }, ((JArray)engine["modules"]).ToObject<string[]>());

        JObject tank = Part(dump, "tank");
        Assert.AreEqual(JTokenType.Null, tank["engine"].Type);          // spec: null when no engine data
        Assert.AreEqual(JTokenType.Null, tank["tech_node"].Type);
        Assert.IsTrue((bool)tank["campaign_unlocked"]);
        Assert.AreEqual("Methane", (string)tank["resources"][0]["name"]);
        Assert.AreEqual(1.6, (double)tank["resources"][0]["capacity"], 1e-9);
        Assert.IsNull(tank["resources"][0]["amount"]);                  // capacity only in a dump
    }

    [Test]
    public void Build_TemplatesAreParsedJsonOrRawText()
    {
        VabDumpPart broken = Tank("broken", true);
        broken.RawJson = "{oops";
        VabDumpPart none = Tank("none", true);
        none.RawJson = null;
        var templates = (JObject)Build(new[] { Engine(), broken, none })["part_templates"];
        Assert.AreEqual("engine_2v_methalox_poodle", (string)templates["engine_2v_methalox_poodle"]["data"]["partName"]);
        Assert.AreEqual(JTokenType.String, templates["broken"].Type);
        Assert.AreEqual("{oops", (string)templates["broken"]);
        Assert.IsNull(templates["none"]);
    }

    [Test]
    public void Build_TemplatesCanBeLimitedToUnlockedParts()
    {
        var templates = (JObject)Build(new[] { Tank("alpha", false), Tank("zeta", true) }, null, true)["part_templates"];
        CollectionAssert.AreEqual(new[] { "zeta" }, templates.Properties().Select(p => p.Name).ToArray());
    }

    // Spec, choix 1: example_assembly is the open rocket's game_assembly, or null with instructions
    // asking for a small rocket first.
    [Test]
    public void Build_ExampleAssembly()
    {
        var example = new JObject { ["parts"] = new JArray(new JObject { ["partName"] = "pod_2v_lander_crew" }) };
        JObject withExample = Build(new[] { Tank("t", true) }, example);
        Assert.IsTrue(JToken.DeepEquals(example, withExample["example_assembly"]));
        Assert.AreEqual(VabDump.Instructions, (string)withExample["instructions_for_ai"]);

        JObject without = Build(new[] { Tank("t", true) });
        Assert.AreEqual(JTokenType.Null, without["example_assembly"].Type);
        StringAssert.Contains("example_assembly is null", (string)without["instructions_for_ai"]);
        StringAssert.Contains("RootGuid", VabDump.Instructions);
        StringAssert.Contains("attachNodeStates", VabDump.Instructions);
        StringAssert.Contains("SymmetrySets", VabDump.Instructions);
    }

    [Test]
    public void Build_CampaignListsTechNodes()
    {
        var campaign = new VabCampaign
        {
            Name = "Skyzer Adventure", Mode = "exploration", UnlockAllParts = true,
            UnlockedTechNodes = { "tNode_1v_start", "tNode_2v_methalox" },
        };
        JObject c = (JObject)Build(new[] { Tank("t", true) }, null, false, campaign)["campaign"];
        Assert.AreEqual("Skyzer Adventure", (string)c["name"]);
        Assert.IsTrue((bool)c["unlock_all_parts"]);
        CollectionAssert.AreEqual(new[] { "tNode_1v_start", "tNode_2v_methalox" },
            ((JArray)c["unlocked_tech_nodes"]).ToObject<string[]>());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run the EditMode test command. Expected: `error CS0246` about `VabDumpPart`, `VabEngineInfo` and `VabDump`.

- [ ] **Step 3: Implement**

`Assets/SkySpaceAgency/Code/VabTools/VabDump.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    public class VabEngineInfo
    {
        public double ThrustVacKN;
        public double ThrustAslKN;
        public double IspVac;
        public double IspAsl;
        public List<string> Propellants = new();
    }

    public class VabDumpPart
    {
        public string Name;
        public string Title;
        public string Category;
        public string Family;
        public string TechNode;          // null when no tech node lists the part
        public string Size;
        public bool Unlocked;            // offered by this VAB (Stats.AvailableParts): what the import accepts
        public bool? CampaignUnlocked;   // SessionManager.IsPartUnlocked; null when unknown
        public double Cost;
        public double MassT;
        public double MaxTemp;
        public double CrashTolerance;
        public int CrewCapacity;
        public List<string> AttachNodes = new();
        public List<VabResource> Resources = new();   // Name and Capacity
        public VabEngineInfo Engine;                  // null when the part has no engine data
        public List<string> Modules = new();
        public string RawJson;                        // Game.Parts.GetRawJson; null when unavailable
    }

    // The parts dump document (spec, "Contenu du dump", and choix 1).
    public static class VabDump
    {
        public const string Format = "SkySpaceAgency.PartsDump";
        public const int Version = 1;

        public const string Instructions =
            "Parts dump of the SkySpaceAgency mod, made in the KSP2 Redux VAB. 'parts' lists every part the game " +
            "knows: \"unlocked\": true means this VAB offers the part now and the mod's Import accepts it; never use " +
            "a part with \"unlocked\": false. Masses are in t, thrust in kN, Isp in s; engine Isp values are the " +
            "definition's atmosphereCurve evaluated at 0 and 1 atm, and thrust_asl_kN is derived from them. " +
            "'part_templates' holds each part's raw definition JSON: its attachNodes (nodeID), resourceContainers " +
            "and modules show how the part is built. To write a rocket the mod can import, produce a " +
            "SerializedAssembly shaped like 'example_assembly' (the game_assembly of the rocket that was open in the " +
            "VAB at dump time): one entry in 'parts' per part, each with its own new PartGuid, its partName and a " +
            "partState; AssemblyOABConfig.RootGuid naming the root part; every other part attached to its parent " +
            "through a pair of attachNodeStates entries, each side naming the other part's PartGuid " +
            "(AttachedPartGuid) and node (attachedPartNodeID); stages in stagingState.availableStages (StageID and " +
            "the PartIds of the stage), mirrored in AssemblyOABConfig.StagingState; symmetric copies listed " +
            "together in SymmetrySets. Copy partState and module state from example_assembly parts of the same " +
            "kind and change only what you need. Send the rocket back as a .json file holding the SerializedAssembly " +
            "at the top level, or inside a SkySpaceAgency export under 'game_assembly'.";

        public const string NoExampleNote =
            " example_assembly is null: no rocket was open in the VAB at dump time. Ask the player to open or " +
            "build a small rocket in the VAB and dump again, or to send a SkySpaceAgency export, and copy its " +
            "structure.";

        public static string InstructionsFor(bool hasExample) => hasExample ? Instructions : Instructions + NoExampleNote;

        public static JObject Build(VabCampaign campaign, DateTime exportedAt, string gameVersion, string modVersion,
                                    IEnumerable<VabDumpPart> parts, JToken exampleAssembly, bool templatesUnlockedOnly)
        {
            List<VabDumpPart> sorted = (parts ?? Enumerable.Empty<VabDumpPart>())
                .Where(p => p?.Name != null)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToList();

            var list = new JArray();
            var templates = new JObject();
            foreach (VabDumpPart p in sorted)
            {
                list.Add(PartJson(p));
                if (p.RawJson == null || (templatesUnlockedOnly && !p.Unlocked) || templates.ContainsKey(p.Name))
                    continue;
                templates[p.Name] = Template(p.RawJson);
            }

            bool hasExample = exampleAssembly != null && exampleAssembly.Type != JTokenType.Null;
            return new JObject
            {
                ["format"] = Format,
                ["version"] = Version,
                ["exported_at"] = VabFiles.IsoTime(exportedAt),
                ["game_version"] = VabNum.Json(gameVersion),
                ["mod_version"] = VabNum.Json(modVersion),
                ["campaign"] = (campaign ?? new VabCampaign()).ToJson(true),
                ["instructions_for_ai"] = InstructionsFor(hasExample),
                ["part_count"] = sorted.Count,
                ["unlocked_count"] = sorted.Count(p => p.Unlocked),
                ["parts"] = list,
                ["part_templates"] = templates,
                ["example_assembly"] = hasExample ? exampleAssembly.DeepClone() : JValue.CreateNull(),
            };
        }

        // The raw definition as JSON, or as text when it does not parse.
        static JToken Template(string raw)
        {
            try { return VabJson.Parse(raw); }
            catch (JsonException) { return new JValue(raw); }
        }

        static JToken EngineJson(VabEngineInfo e)
        {
            if (e == null)
                return JValue.CreateNull();
            return new JObject
            {
                ["thrust_vac_kN"] = VabNum.Round(e.ThrustVacKN, VabSummary.ThrustDigits),
                ["thrust_asl_kN"] = VabNum.Round(e.ThrustAslKN, VabSummary.ThrustDigits),
                ["isp_vac"] = VabNum.Round(e.IspVac, VabSummary.IspDigits),
                ["isp_asl"] = VabNum.Round(e.IspAsl, VabSummary.IspDigits),
                ["propellants"] = new JArray(e.Propellants ?? new List<string>()),
            };
        }

        static JObject PartJson(VabDumpPart p)
        {
            var resources = new JArray();
            foreach (VabResource r in p.Resources)
                resources.Add(new JObject
                {
                    ["name"] = VabNum.Json(r.Name),
                    ["capacity"] = VabNum.Round(r.Capacity, VabSummary.AmountDigits),
                });
            return new JObject
            {
                ["name"] = p.Name,
                ["title"] = VabNum.Json(p.Title),
                ["category"] = VabNum.Json(p.Category),
                ["family"] = VabNum.Json(p.Family),
                ["tech_node"] = VabNum.Json(p.TechNode),
                ["unlocked"] = p.Unlocked,
                ["campaign_unlocked"] = p.CampaignUnlocked.HasValue ? new JValue(p.CampaignUnlocked.Value) : JValue.CreateNull(),
                ["cost"] = VabNum.Round(p.Cost, VabSummary.CostDigits),
                ["mass_t"] = VabNum.Round(p.MassT, VabSummary.MassDigits),
                ["size"] = VabNum.Json(p.Size),
                ["crew_capacity"] = p.CrewCapacity,
                ["max_temp"] = VabNum.Round(p.MaxTemp, 0),
                ["crash_tolerance"] = VabNum.Round(p.CrashTolerance, 1),
                ["attach_nodes"] = new JArray(p.AttachNodes.Where(n => n != null).ToList()),
                ["resources"] = resources,
                ["engine"] = EngineJson(p.Engine),
                ["modules"] = new JArray(p.Modules),
            };
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the EditMode test command. Expected: `exit=0` and `failed="0"`, with 6 more tests (total 156).

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/VabTools/VabDump.cs* Assets/Tests/Vab/VabDumpTests.cs*
git status --short   # only these paths
git commit -m "Add the VAB parts dump document with templates and an example assembly

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 5: `VabImport`: formats, structure, missing parts

**Files:**
- Create: `Assets/SkySpaceAgency/Code/VabTools/VabImport.cs`
- Test: `Assets/Tests/Vab/VabFixtures.cs`, `Assets/Tests/Vab/VabImportTests.cs`

**Interfaces:**
- Consumes: `VabExport.Format`, `VabDump.Format`, `VabJson.*`.
- Produces:
  - `enum VabImportFormat { ModExport, RawAssembly, WorkspaceSnapshot }`;
  - `VabImportFile { Format, Root, Assembly, Assemblies }`. `Assembly` is the one to load as main, null for a workspace file. `Assemblies` lists every assembly to check, as live nodes of `Root`;
  - `VabImportResult { File, Error, Ok }`, `VabMissingPart { Name, Known }`;
  - `VabImport.Parse(string) : VabImportResult`, `CheckStructure(JObject) : List<string>` (at most `MaxErrors` + 1 lines), `PartNames(IEnumerable<JObject>) : List<string>`;
  - `VabImport.MissingParts(IEnumerable<string>, ICollection<string> available, ICollection<string> known) : List<VabMissingPart>`, `DescribeMissing(IList<VabMissingPart>) : string`.
- `ClampResources` is added in Task 6, in the same file.

The fixtures copy the shape of a real workspace file. They come from `Workspaces/fusée pour orbite V2.json` (2026-10-16): `parts[].PartGuid.Guid`, `partName`, `partState.attachNodeStates[]` (`nodeId`, `AttachedPartGuid.Guid`, `attachedPartNodeID`), `partState.resources.<name>` (`name`, `storedUnits`, `capacityUnits`), `PartSwitchOverrides`, `partState.partPropertyOverrides`, `AssemblyOABConfig.RootGuid.Guid`, and for workspaces `Assemblies[]` (`assembly`, `Assembly`, `isMainAssembly`, `position`, `boundsSize`).

- [ ] **Step 1: Write the fixtures and the failing tests**

`Assets/Tests/Vab/VabFixtures.cs`:

```csharp
using Newtonsoft.Json.Linq;

// SerializedAssembly-shaped JSON trees with the fields the VAB tools read. Shape taken from a real VAB
// workspace file of 2026-10-16 ("fusée pour orbite V2.json"); GUIDs from that file.
public static class VabFixtures
{
    public const string PodGuid = "8dda8ca2-e636-41fa-bb90-0ddf0c1c25dd";
    public const string TankGuid = "8f48c048-5c1e-4bf3-90c9-a81874d16a19";
    public const string EngineGuid = "caf67144-78bd-4747-ab17-362337921716";
    public const string DetachedGuid = "a70f45c8-ae64-4d3a-8ee5-3e923895e4e7";
    public const string NewGuid = "76806f3c-8248-4e80-adae-3d9341fd588e";
    public const string ZeroGuid = "00000000-0000-0000-0000-000000000000";

    public const string Pod = "pod_2v_lander_crew";
    public const string Tank = "fueltank_2v_inline_methalox_1x2";
    public const string Engine = "engine_2v_methalox_poodle";

    public static JObject Id(string guid) => new JObject { ["Guid"] = guid, ["DebugName"] = JValue.CreateNull() };

    public static JObject Node(string nodeId, string otherGuid, string otherNode) => new JObject
    {
        ["nodeId"] = nodeId,
        ["IsDynamic"] = false,
        ["attachedPartGuid"] = JValue.CreateNull(),
        ["AttachedPartGuid"] = Id(otherGuid ?? ZeroGuid),
        ["attachedPartNodeID"] = otherNode ?? "",
    };

    public static JObject Resource(string name, double stored, double capacity) => new JObject
    {
        ["name"] = name, ["storedUnits"] = stored, ["capacityUnits"] = capacity,
    };

    public static JObject Part(string guid, string name, JArray nodes, JObject resources = null) => new JObject
    {
        ["partGuid"] = JValue.CreateNull(),
        ["PartGuid"] = Id(guid),
        ["partName"] = name,
        ["partState"] = new JObject
        {
            ["partStatus"] = "NONE",
            ["attachNodeStates"] = nodes ?? new JArray(),
            ["resources"] = (JToken)resources ?? JValue.CreateNull(),
            ["StageIndex"] = 0,
            ["Temperature"] = "-Infinity",
            ["partPropertyOverrides"] = JValue.CreateNull(),
        },
        ["PartModulesState"] = new JArray(),
        ["PartSwitchOverrides"] = JValue.CreateNull(),
    };

    // pod (root) - top/bottom - tank - bottom/top - engine, as in the real file.
    public static JObject Rocket()
    {
        JObject pod = Part(PodGuid, Pod, new JArray(Node("bottom", TankGuid, "top")));
        JObject tank = Part(TankGuid, Tank,
            new JArray(Node("top", PodGuid, "bottom"), Node("bottom", EngineGuid, "top"), Node("srfAttach", null, null)),
            new JObject { ["Methane"] = Resource("Methane", 1.6, 1.6), ["Oxidizer"] = Resource("Oxidizer", 6.4, 6.4) });
        JObject engine = Part(EngineGuid, Engine, new JArray(Node("top", TankGuid, "bottom")));
        return new JObject
        {
            ["Version"] = "0.1",
            ["AssemblyOABConfig"] = new JObject { ["RootGuid"] = Id(PodGuid) },
            ["AssemblyDefinition"] = new JObject { ["assemblyName"] = "Bon vol" },
            ["parts"] = new JArray(pod, tank, engine),
        };
    }

    public static JObject SinglePart(string guid, string name) => new JObject
    {
        ["AssemblyOABConfig"] = new JObject { ["RootGuid"] = Id(guid) },
        ["parts"] = new JArray(Part(guid, name, null)),
    };

    // An OABHistoricalSnapshot as the game writes it (workspace file, before_import backup).
    public static JObject Workspace(params (JObject assembly, bool main)[] entries)
    {
        var list = new JArray();
        foreach (var (assembly, main) in entries)
            list.Add(new JObject
            {
                ["assembly"] = "",
                ["Assembly"] = assembly,
                ["isMainAssembly"] = main,
                ["position"] = new JObject { ["x"] = 10.7356882, ["y"] = 41.5753136, ["z"] = 51.5100632 },
                ["boundsSize"] = new JObject { ["x"] = 2.78, ["y"] = 6.12, ["z"] = 2.78 },
            });
        return new JObject
        {
            ["Metadata"] = new JObject { ["VehicleName"] = "Bon vol" },
            ["ActionType"] = "None",
            ["oabOrientation"] = "VAB",
            ["Assemblies"] = list,
        };
    }
}
```

`Assets/Tests/Vab/VabImportTests.cs`:

```csharp
using System.Collections.Generic;
using K2D2.VabTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using F = VabFixtures;

public class VabImportTests
{
    static bool Any(List<string> errors, string text) => errors.Exists(e => e.Contains(text));

    // Spec test: detection of the two formats (plus the workspace file, deviation 1).
    [Test]
    public void Parse_RawAssembly()
    {
        VabImportResult r = VabImport.Parse(VabJson.Write(F.Rocket()));
        Assert.IsTrue(r.Ok, r.Error);
        Assert.AreEqual(VabImportFormat.RawAssembly, r.File.Format);
        Assert.AreEqual(1, r.File.Assemblies.Count);
        Assert.AreSame(r.File.Assembly, r.File.Assemblies[0]);
        Assert.AreEqual(3, ((JArray)r.File.Assembly["parts"]).Count);
    }

    [Test]
    public void Parse_ModExport()
    {
        var export = new JObject
        {
            ["format"] = VabExport.Format, ["version"] = 1, ["summary"] = new JObject(), ["game_assembly"] = F.Rocket(),
        };
        VabImportResult r = VabImport.Parse(VabJson.Write(export));
        Assert.IsTrue(r.Ok, r.Error);
        Assert.AreEqual(VabImportFormat.ModExport, r.File.Format);
        Assert.AreEqual("Bon vol", (string)r.File.Assembly["AssemblyDefinition"]["assemblyName"]);

        // An AI may hand game_assembly back as a JSON string.
        export["game_assembly"] = VabJson.WriteCompact(F.Rocket());
        r = VabImport.Parse(VabJson.Write(export));
        Assert.IsTrue(r.Ok, r.Error);
        Assert.AreEqual(3, ((JArray)r.File.Assembly["parts"]).Count);
    }

    [Test]
    public void Parse_WorkspaceSnapshot()
    {
        JObject ws = F.Workspace((F.Rocket(), true), (F.SinglePart(F.DetachedGuid, "antenna_0v_16"), false));
        VabImportResult r = VabImport.Parse(VabJson.Write(ws));
        Assert.IsTrue(r.Ok, r.Error);
        Assert.AreEqual(VabImportFormat.WorkspaceSnapshot, r.File.Format);
        Assert.AreEqual(2, r.File.Assemblies.Count);
        Assert.IsNull(r.File.Assembly);
        Assert.IsNotNull(r.File.Root["Assemblies"]);
    }

    // Spec test: refusal of an invalid JSON.
    [Test]
    public void Parse_RejectsInvalidJson()
    {
        StringAssert.StartsWith("Not valid JSON", VabImport.Parse("{ \"parts\": [ ").Error);
        StringAssert.StartsWith("Not valid JSON", VabImport.Parse("").Error);
        StringAssert.StartsWith("Not valid JSON", VabImport.Parse(null).Error);
    }

    [Test]
    public void Parse_RejectsWrongShapes()
    {
        StringAssert.StartsWith("Not a JSON object", VabImport.Parse("[1, 2]").Error);
        StringAssert.StartsWith("Unrecognized JSON", VabImport.Parse("{\"a\": 1}").Error);
        StringAssert.Contains("parts dump", VabImport.Parse("{\"format\": \"SkySpaceAgency.PartsDump\"}").Error);
        StringAssert.Contains("game_assembly",
            VabImport.Parse("{\"format\": \"SkySpaceAgency.VabExport\", \"game_assembly\": null}").Error);
        StringAssert.Contains("game_assembly is not valid JSON",
            VabImport.Parse("{\"format\": \"SkySpaceAgency.VabExport\", \"game_assembly\": \"{oops\"}").Error);
        StringAssert.StartsWith("Workspace entry #1", VabImport.Parse("{\"Assemblies\": [ {\"isMainAssembly\": true} ]}").Error);
        StringAssert.Contains("no assembly", VabImport.Parse("{\"Assemblies\": []}").Error);
    }

    [Test]
    public void CheckStructure_AcceptsAConsistentAssembly()
    {
        CollectionAssert.IsEmpty(VabImport.CheckStructure(F.Rocket()));
        JObject noConfig = F.Rocket();
        noConfig.Remove("AssemblyOABConfig");   // the game takes the root from the parts then
        CollectionAssert.IsEmpty(VabImport.CheckStructure(noConfig));
    }

    [Test]
    public void CheckStructure_ReportsBrokenLinks()
    {
        JObject duplicate = F.Rocket();
        duplicate["parts"][2]["PartGuid"]["Guid"] = F.PodGuid;
        List<string> errors = VabImport.CheckStructure(duplicate);
        Assert.IsTrue(Any(errors, $"PartGuid {F.PodGuid} is used by more than one part."), string.Join("\n", errors));

        JObject badRoot = F.Rocket();
        badRoot["AssemblyOABConfig"]["RootGuid"]["Guid"] = F.NewGuid;
        errors = VabImport.CheckStructure(badRoot);
        Assert.IsTrue(Any(errors, $"AssemblyOABConfig.RootGuid ({F.NewGuid}) is not one of the parts."), string.Join("\n", errors));

        JObject dangling = F.Rocket();
        ((JArray)dangling["parts"]).RemoveAt(2);  // engine removed, the tank's bottom node still points to it
        errors = VabImport.CheckStructure(dangling);
        CollectionAssert.AreEqual(new[]
        {
            $"Part #2 ({F.Tank}) node \"bottom\" points to part {F.EngineGuid}, which is not in the assembly.",
        }, errors);
    }

    [Test]
    public void CheckStructure_ReportsMissingFields()
    {
        CollectionAssert.AreEqual(new[] { "The assembly has no \"parts\" list." }, VabImport.CheckStructure(new JObject()));
        CollectionAssert.AreEqual(new[] { "The assembly has no \"parts\" list." }, VabImport.CheckStructure(null));
        CollectionAssert.AreEqual(new[] { "The assembly has no part." },
            VabImport.CheckStructure(new JObject { ["parts"] = new JArray() }));

        JObject noName = F.Rocket();
        noName["parts"][1]["partName"] = "";
        Assert.IsTrue(Any(VabImport.CheckStructure(noName), "Part #2 has no partName."));

        JObject badGuid = F.Rocket();
        badGuid["parts"][2]["PartGuid"]["Guid"] = "not-a-guid";
        Assert.IsTrue(Any(VabImport.CheckStructure(badGuid), $"Part #3 ({F.Engine}) has no valid PartGuid."));
    }

    [Test]
    public void CheckStructure_CapsTheErrorList()
    {
        var parts = new JArray();
        for (int i = 0; i < 15; i++)
            parts.Add(F.Part(F.PodGuid, "x", null));   // 14 duplicates
        var assembly = new JObject { ["AssemblyOABConfig"] = new JObject { ["RootGuid"] = F.Id(F.PodGuid) }, ["parts"] = parts };
        List<string> errors = VabImport.CheckStructure(assembly);
        Assert.AreEqual(VabImport.MaxErrors + 1, errors.Count);
        StringAssert.StartsWith("... and 4 more", errors[errors.Count - 1]);
    }

    [Test]
    public void PartNames_AreDistinctInOrder()
    {
        CollectionAssert.AreEqual(new[] { F.Pod, F.Tank, F.Engine },
            VabImport.PartNames(new[] { F.Rocket(), F.SinglePart(F.DetachedGuid, F.Pod) }));
    }

    // Spec test: list of the missing parts (name + unlocked or not).
    [Test]
    public void MissingParts_ClassifiesLockedAndUnknown()
    {
        List<VabMissingPart> missing = VabImport.MissingParts(
            new[] { F.Pod, F.Tank, F.Engine, "mystery_part" },
            new HashSet<string> { F.Pod, F.Tank },
            new HashSet<string> { F.Pod, F.Tank, F.Engine });
        Assert.AreEqual(2, missing.Count);
        Assert.AreEqual(F.Engine, missing[0].Name);
        Assert.IsTrue(missing[0].Known);
        Assert.AreEqual("mystery_part", missing[1].Name);
        Assert.IsFalse(missing[1].Known);
        Assert.AreEqual(
            "Refused: 2 part(s) not available in this VAB: engine_2v_methalox_poodle (not unlocked), " +
            "mystery_part (unknown part). Your rocket was not changed.",
            VabImport.DescribeMissing(missing));
    }

    [Test]
    public void MissingParts_NoneWhenAllAvailable()
    {
        CollectionAssert.IsEmpty(VabImport.MissingParts(
            new[] { F.Pod, F.Pod }, new HashSet<string> { F.Pod }, new HashSet<string>()));
    }
}
```

`using F = VabFixtures;` is an alias to a type in the global namespace. It is legal and keeps the lines short.

- [ ] **Step 2: Run the tests to verify they fail**

Run the EditMode test command. Expected: `error CS0246` / `CS0103` about `VabImport`, `VabImportResult`, `VabImportFormat` and `VabMissingPart`.

- [ ] **Step 3: Implement**

`Assets/SkySpaceAgency/Code/VabTools/VabImport.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    public enum VabImportFormat { ModExport, RawAssembly, WorkspaceSnapshot }

    public class VabImportFile
    {
        public VabImportFormat Format;
        public JObject Root;                      // the whole file
        public JObject Assembly;                  // the assembly to load as main; null for a workspace file
        public List<JObject> Assemblies = new();  // every assembly to check (live nodes of Root)
    }

    public class VabImportResult
    {
        public VabImportFile File;
        public string Error;
        public bool Ok => Error == null;
    }

    public class VabMissingPart
    {
        public string Name;
        public bool Known;   // a part of the game, not offered by this VAB; false = unknown name
    }

    // Everything the import decides before the game is called (spec, "Import" and choix 2).
    public static class VabImport
    {
        public const int MaxErrors = 10;

        public static VabImportResult Parse(string text)
        {
            JToken token;
            try { token = VabJson.Parse(text); }
            catch (JsonException e) { return Fail("Not valid JSON: " + e.Message); }

            if (!(token is JObject root))
                return Fail("Not a JSON object.");

            string format = VabJson.Str(root["format"]);
            if (format == VabExport.Format)
            {
                JToken embedded = root["game_assembly"];
                if (embedded != null && embedded.Type == JTokenType.String)
                {
                    try { embedded = VabJson.Parse((string)embedded); }
                    catch (JsonException e) { return Fail("game_assembly is not valid JSON: " + e.Message); }
                }
                if (!(embedded is JObject assembly) || !(assembly["parts"] is JArray))
                    return Fail("This export has no game_assembly with a \"parts\" list.");
                return Done(VabImportFormat.ModExport, root, assembly, new List<JObject> { assembly });
            }
            if (format == VabDump.Format)
                return Fail("This is a parts dump, not a rocket: import an export or a game assembly.");

            if (root["Assemblies"] is JArray entries)
            {
                var assemblies = new List<JObject>();
                for (int i = 0; i < entries.Count; i++)
                {
                    if (!(entries[i] is JObject entry) || !(entry["Assembly"] is JObject a) || !(a["parts"] is JArray))
                        return Fail($"Workspace entry #{i + 1} has no Assembly with a \"parts\" list.");
                    assemblies.Add(a);
                }
                if (assemblies.Count == 0)
                    return Fail("This workspace file has no assembly.");
                return Done(VabImportFormat.WorkspaceSnapshot, root, null, assemblies);
            }

            if (root["parts"] is JArray)
                return Done(VabImportFormat.RawAssembly, root, root, new List<JObject> { root });

            return Fail("Unrecognized JSON: expected a SkySpaceAgency export, a game assembly (with \"parts\") " +
                        "or a VAB workspace file (with \"Assemblies\").");
        }

        static VabImportResult Fail(string error) => new VabImportResult { Error = error };

        static VabImportResult Done(VabImportFormat format, JObject root, JObject assembly, List<JObject> assemblies) =>
            new VabImportResult
            {
                File = new VabImportFile { Format = format, Root = root, Assembly = assembly, Assemblies = assemblies },
            };

        // Problems the game's loader would trip on: names, GUIDs, root, node pairs. Empty when fine.
        public static List<string> CheckStructure(JObject assembly)
        {
            var errors = new List<string>();
            if (!(assembly?["parts"] is JArray parts))
            {
                errors.Add("The assembly has no \"parts\" list.");
                return errors;
            }
            if (parts.Count == 0)
            {
                errors.Add("The assembly has no part.");
                return errors;
            }

            var guids = new HashSet<string>();
            for (int i = 0; i < parts.Count; i++)
            {
                if (!(parts[i] is JObject part))
                {
                    errors.Add($"Part #{i + 1} is not a JSON object.");
                    continue;
                }
                string name = VabJson.Str(part["partName"]);
                if (string.IsNullOrWhiteSpace(name))
                    errors.Add($"Part #{i + 1} has no partName.");
                string guid = VabJson.GuidOf(part["PartGuid"]);
                if (guid == null || guid == VabJson.ZeroGuid)
                    errors.Add($"Part #{i + 1} ({name ?? "?"}) has no valid PartGuid.");
                else if (!guids.Add(guid))
                    errors.Add($"PartGuid {guid} is used by more than one part.");
            }

            // Like the game's IsRootResolvable: with a saved configuration, its root must be a part.
            if (assembly["AssemblyOABConfig"] is JObject config)
            {
                string rootGuid = VabJson.GuidOf(config["RootGuid"]);
                if (rootGuid == null || !guids.Contains(rootGuid))
                    errors.Add($"AssemblyOABConfig.RootGuid ({rootGuid ?? "missing"}) is not one of the parts.");
            }

            for (int i = 0; i < parts.Count; i++)
            {
                if (!(parts[i] is JObject part) || !(part["partState"] is JObject state) ||
                    !(state["attachNodeStates"] is JArray nodes))
                    continue;
                string name = VabJson.Str(part["partName"]) ?? "?";
                foreach (JToken n in nodes)
                {
                    if (!(n is JObject node))
                        continue;
                    string other = VabJson.GuidOf(node["AttachedPartGuid"]);
                    if (other == null || other == VabJson.ZeroGuid || guids.Contains(other))
                        continue;
                    errors.Add($"Part #{i + 1} ({name}) node \"{VabJson.Str(node["nodeId"]) ?? "?"}\" points to part " +
                               $"{other}, which is not in the assembly.");
                }
            }

            if (errors.Count > MaxErrors)
            {
                int extra = errors.Count - MaxErrors;
                errors = errors.Take(MaxErrors).ToList();
                errors.Add($"... and {extra} more problem(s).");
            }
            return errors;
        }

        // Distinct part names, in order of first appearance.
        public static List<string> PartNames(IEnumerable<JObject> assemblies)
        {
            var seen = new HashSet<string>();
            var names = new List<string>();
            foreach (JObject assembly in assemblies)
            {
                if (!(assembly?["parts"] is JArray parts))
                    continue;
                foreach (JToken p in parts)
                {
                    string name = p is JObject part ? VabJson.Str(part["partName"]) : null;
                    if (!string.IsNullOrEmpty(name) && seen.Add(name))
                        names.Add(name);
                }
            }
            return names;
        }

        // Names not offered by this VAB. Known = the game has the part (locked in this campaign,
        // or not for this builder); unknown = no such part.
        public static List<VabMissingPart> MissingParts(IEnumerable<string> names, ICollection<string> available,
                                                        ICollection<string> known)
        {
            var missing = new List<VabMissingPart>();
            var seen = new HashSet<string>();
            foreach (string name in names)
            {
                if (name == null || !seen.Add(name) || available.Contains(name))
                    continue;
                missing.Add(new VabMissingPart { Name = name, Known = known != null && known.Contains(name) });
            }
            return missing;
        }

        public static string DescribeMissing(IList<VabMissingPart> missing) =>
            $"Refused: {missing.Count} part(s) not available in this VAB: " +
            string.Join(", ", missing.Select(m => m.Name + (m.Known ? " (not unlocked)" : " (unknown part)"))) +
            ". Your rocket was not changed.";
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the EditMode test command. Expected: `exit=0` and `failed="0"`, with 12 more tests (total 168).

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/VabTools/VabImport.cs* Assets/Tests/Vab/VabFixtures.cs* Assets/Tests/Vab/VabImportTests.cs*
git status --short   # only these paths
git commit -m "Add VAB import checks: formats, structure and parts not offered by the VAB

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 6: Resource clamping and `VabSnapshot`

**Files:**
- Modify: `Assets/SkySpaceAgency/Code/VabTools/VabImport.cs`
- Create: `Assets/SkySpaceAgency/Code/VabTools/VabSnapshot.cs`
- Test: `Assets/Tests/Vab/VabClampTests.cs`, `Assets/Tests/Vab/VabSnapshotTests.cs`

**Interfaces:**
- Produces:
  - `VabImport.ClampResources(JObject assembly, Func<string, string, double?> definitionCapacity) : List<string>`. It edits the tree in place and returns one line per correction, such as `"<partName> (part #i) <resource>: amount 5 -> 1.6 (capacity)"`. `definitionCapacity(partName, resourceName)` is null when unknown; the whole function may be null;
  - `VabSnapshot.MainEntry(JObject snapshot) : JObject`, `ReplaceMain(JObject snapshot, JObject assembly) : bool` (false, unchanged, when there is no main entry);
  - `VabSnapshot.PartCount(JObject assembly)`, `MainPartCount(JObject snapshot)`, `TotalPartCount(JObject snapshot)`.

- [ ] **Step 1: Write the failing tests**

`Assets/Tests/Vab/VabClampTests.cs`:

```csharp
using System.Collections.Generic;
using K2D2.VabTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using F = VabFixtures;

// Spec, choix 2: an amount above the part's capacity is lowered to the capacity and reported.
public class VabClampTests
{
    static JObject Res(JObject rocket, string name) => (JObject)rocket["parts"][1]["partState"]["resources"][name];

    static double? Definition(string part, string resource) =>
        part == F.Tank ? resource == "Methane" ? 1.6 : resource == "Oxidizer" ? 6.4 : (double?)null : null;

    [Test]
    public void Clamp_LowersAmountAboveCapacity()
    {
        JObject rocket = F.Rocket();
        Res(rocket, "Methane")["storedUnits"] = 5.0;
        List<string> corrections = VabImport.ClampResources(rocket, null);
        CollectionAssert.AreEqual(new[] { $"{F.Tank} (part #2) Methane: amount 5 -> 1.6 (capacity)" }, corrections);
        Assert.AreEqual(1.6, (double)Res(rocket, "Methane")["storedUnits"], 1e-12);
    }

    [Test]
    public void Clamp_LowersCapacityAboveTheDefinition()
    {
        JObject rocket = F.Rocket();
        Res(rocket, "Oxidizer")["storedUnits"] = 100.0;
        Res(rocket, "Oxidizer")["capacityUnits"] = 100.0;
        List<string> corrections = VabImport.ClampResources(rocket, Definition);
        CollectionAssert.AreEqual(new[]
        {
            $"{F.Tank} (part #2) Oxidizer: capacity 100 -> 6.4 (part definition)",
            $"{F.Tank} (part #2) Oxidizer: amount 100 -> 6.4 (capacity)",
        }, corrections);
        Assert.AreEqual(6.4, (double)Res(rocket, "Oxidizer")["capacityUnits"], 1e-12);
        Assert.AreEqual(6.4, (double)Res(rocket, "Oxidizer")["storedUnits"], 1e-12);
    }

    [Test]
    public void Clamp_SkipsTheDefinitionForVariantParts()
    {
        JObject rocket = F.Rocket();
        rocket["parts"][1]["PartSwitchOverrides"] = new JObject { ["variant"] = "long" };
        Res(rocket, "Oxidizer")["storedUnits"] = 100.0;
        Res(rocket, "Oxidizer")["capacityUnits"] = 100.0;
        CollectionAssert.IsEmpty(VabImport.ClampResources(rocket, Definition));
        Assert.AreEqual(100.0, (double)Res(rocket, "Oxidizer")["storedUnits"], 1e-12);
    }

    [Test]
    public void Clamp_FixesNegativeAmountsAndLeavesValidOnes()
    {
        JObject rocket = F.Rocket();
        JToken before = rocket.DeepClone();
        CollectionAssert.IsEmpty(VabImport.ClampResources(rocket, Definition));
        Assert.IsTrue(JToken.DeepEquals(before, rocket));

        Res(rocket, "Methane")["storedUnits"] = -1.0;
        CollectionAssert.AreEqual(new[] { $"{F.Tank} (part #2) Methane: amount -1 -> 0" },
            VabImport.ClampResources(rocket, Definition));
        Assert.AreEqual(0.0, (double)Res(rocket, "Methane")["storedUnits"], 1e-12);
    }

    [Test]
    public void Clamp_UsesTheDefinitionWhenTheFileHasNoCapacity()
    {
        JObject rocket = F.Rocket();
        Res(rocket, "Methane").Remove("capacityUnits");
        Res(rocket, "Methane")["storedUnits"] = 10.0;
        CollectionAssert.AreEqual(new[] { $"{F.Tank} (part #2) Methane: amount 10 -> 1.6 (capacity)" },
            VabImport.ClampResources(rocket, Definition));
    }
}
```

`Assets/Tests/Vab/VabSnapshotTests.cs`:

```csharp
using K2D2.VabTools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using F = VabFixtures;

// Spec, choix 3: only the main rocket is replaced, detached assemblies stay.
public class VabSnapshotTests
{
    [Test]
    public void ReplaceMain_ReplacesOnlyTheMainAssembly()
    {
        JObject ws = F.Workspace((F.SinglePart(F.DetachedGuid, "antenna_0v_16"), false), (F.Rocket(), true));
        JObject imported = F.SinglePart(F.NewGuid, "pod_1v_conic");
        Assert.IsTrue(VabSnapshot.ReplaceMain(ws, imported));

        JToken main = ws["Assemblies"][1];
        Assert.IsTrue(JToken.DeepEquals(imported, main["Assembly"]));
        Assert.AreNotSame(imported, main["Assembly"]);
        Assert.IsTrue((bool)main["isMainAssembly"]);
        Assert.AreEqual(10.7356882, (double)main["position"]["x"], 1e-9);   // placement kept
        Assert.IsTrue(JToken.DeepEquals(F.SinglePart(F.DetachedGuid, "antenna_0v_16"), ws["Assemblies"][0]["Assembly"]));

        imported["parts"][0]["partName"] = "changed";
        Assert.AreEqual("pod_1v_conic", (string)ws["Assemblies"][1]["Assembly"]["parts"][0]["partName"]);
    }

    [Test]
    public void ReplaceMain_WithoutMainChangesNothing()
    {
        JObject ws = F.Workspace((F.Rocket(), false));
        JToken before = ws.DeepClone();
        Assert.IsFalse(VabSnapshot.ReplaceMain(ws, F.SinglePart(F.NewGuid, "pod_1v_conic")));
        Assert.IsTrue(JToken.DeepEquals(before, ws));
        Assert.IsNull(VabSnapshot.MainEntry(ws));
        Assert.IsNull(VabSnapshot.MainEntry(null));
    }

    [Test]
    public void CountsParts()
    {
        JObject ws = F.Workspace((F.SinglePart(F.DetachedGuid, "antenna_0v_16"), false), (F.Rocket(), true));
        Assert.AreEqual(3, VabSnapshot.PartCount(F.Rocket()));
        Assert.AreEqual(0, VabSnapshot.PartCount(new JObject()));
        Assert.AreEqual(3, VabSnapshot.MainPartCount(ws));
        Assert.AreEqual(4, VabSnapshot.TotalPartCount(ws));
        Assert.AreEqual(0, VabSnapshot.MainPartCount(F.Workspace((F.Rocket(), false))));
        Assert.AreEqual(0, VabSnapshot.TotalPartCount(new JObject()));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run the EditMode test command. Expected: `error CS0117` (`VabImport` has no `ClampResources`) and `CS0103` about `VabSnapshot`.

- [ ] **Step 3: Implement**

In `Assets/SkySpaceAgency/Code/VabTools/VabImport.cs`, replace the usings:

```csharp
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
```

with:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
```

Then replace the end of the class:

```csharp
        public static string DescribeMissing(IList<VabMissingPart> missing) =>
            $"Refused: {missing.Count} part(s) not available in this VAB: " +
            string.Join(", ", missing.Select(m => m.Name + (m.Known ? " (not unlocked)" : " (unknown part)"))) +
            ". Your rocket was not changed.";
    }
}
```

with:

```csharp
        public static string DescribeMissing(IList<VabMissingPart> missing) =>
            $"Refused: {missing.Count} part(s) not available in this VAB: " +
            string.Join(", ", missing.Select(m => m.Name + (m.Known ? " (not unlocked)" : " (unknown part)"))) +
            ". Your rocket was not changed.";

        // Spec, choix 2: lowers, in place, every resource amount above its capacity, and every
        // capacity above the part definition's (deviation 3). The capacity is checked against the
        // definition only for parts without variant or property overrides, whose definition
        // capacity may not apply. Returns one line per correction, for the log and the status line.
        public static List<string> ClampResources(JObject assembly, Func<string, string, double?> definitionCapacity)
        {
            var corrections = new List<string>();
            if (!(assembly?["parts"] is JArray parts))
                return corrections;
            for (int i = 0; i < parts.Count; i++)
            {
                if (!(parts[i] is JObject part) || !(part["partState"] is JObject state) ||
                    !(state["resources"] is JObject resources))
                    continue;
                string partName = VabJson.Str(part["partName"]) ?? "?";
                bool overridden = HasContent(part["PartSwitchOverrides"]) || HasContent(state["partPropertyOverrides"]);
                foreach (JProperty property in resources.Properties())
                {
                    if (!(property.Value is JObject resource))
                        continue;
                    string resourceName = VabJson.Str(resource["name"]) ?? property.Name;
                    string where = $"{partName} (part #{i + 1}) {resourceName}";
                    double? stored = VabJson.Num(resource["storedUnits"]);
                    double? capacity = VabJson.Num(resource["capacityUnits"]);
                    double? definition = overridden || definitionCapacity == null
                        ? null
                        : definitionCapacity(partName, resourceName);

                    if (definition.HasValue && capacity.HasValue && capacity.Value > definition.Value + Tolerance(definition.Value))
                    {
                        corrections.Add($"{where}: capacity {VabNum.Text(capacity.Value)} -> {VabNum.Text(definition.Value)} (part definition)");
                        resource["capacityUnits"] = definition.Value;
                        capacity = definition;
                    }

                    double? limit = capacity ?? definition;
                    if (stored.HasValue && limit.HasValue && stored.Value > limit.Value + Tolerance(limit.Value))
                    {
                        corrections.Add($"{where}: amount {VabNum.Text(stored.Value)} -> {VabNum.Text(limit.Value)} (capacity)");
                        resource["storedUnits"] = limit.Value;
                    }
                    else if (stored.HasValue && stored.Value < 0)
                    {
                        corrections.Add($"{where}: amount {VabNum.Text(stored.Value)} -> 0");
                        resource["storedUnits"] = 0.0;
                    }
                }
            }
            return corrections;
        }

        // Float noise between the file (single precision) and the definition (double) is not a correction.
        static double Tolerance(double value) => 1e-6 * Math.Max(1.0, Math.Abs(value));

        static bool HasContent(JToken token) =>
            token != null && token.Type != JTokenType.Null && !(token is JContainer && !token.HasValues);
    }
}
```

`Assets/SkySpaceAgency/Code/VabTools/VabSnapshot.cs`:

```csharp
using Newtonsoft.Json.Linq;

namespace K2D2.VabTools
{
    // A VAB workspace snapshot (OABHistoricalSnapshot as JSON): {"Assemblies": [{"Assembly": ..., "isMainAssembly": ...,
    // "position": ..., "boundsSize": ...}], ...}.
    public static class VabSnapshot
    {
        public static JObject MainEntry(JObject snapshot)
        {
            if (!(snapshot?["Assemblies"] is JArray list))
                return null;
            foreach (JToken e in list)
                if (e is JObject entry && entry["isMainAssembly"]?.Type == JTokenType.Boolean && (bool)entry["isMainAssembly"])
                    return entry;
            return null;
        }

        // Puts a copy of assembly in place of the main entry's, keeping its placement and the other
        // (detached) assemblies. False, and nothing changed, when the snapshot has no main entry.
        public static bool ReplaceMain(JObject snapshot, JObject assembly)
        {
            JObject entry = MainEntry(snapshot);
            if (entry == null || assembly == null)
                return false;
            entry["Assembly"] = assembly.DeepClone();
            if (entry["assembly"]?.Type == JTokenType.String)
                entry["assembly"] = "";   // obsolete string form: never let it shadow the new assembly
            return true;
        }

        public static int PartCount(JObject assembly) => assembly?["parts"] is JArray parts ? parts.Count : 0;

        public static int MainPartCount(JObject snapshot) =>
            MainEntry(snapshot)?["Assembly"] is JObject assembly ? PartCount(assembly) : 0;

        public static int TotalPartCount(JObject snapshot)
        {
            int total = 0;
            if (snapshot?["Assemblies"] is JArray list)
                foreach (JToken e in list)
                    if (e is JObject entry && entry["Assembly"] is JObject assembly)
                        total += PartCount(assembly);
            return total;
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the EditMode test command. Expected: `exit=0` and `failed="0"`, with 8 more tests (total 176).

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/VabTools/VabImport.cs Assets/SkySpaceAgency/Code/VabTools/VabSnapshot.cs* Assets/Tests/Vab/VabClampTests.cs* Assets/Tests/Vab/VabSnapshotTests.cs*
git status --short   # only these paths
git commit -m "Clamp imported resources to capacity and swap the main rocket in a workspace snapshot

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 7: `VabReader` (game → DTOs)

**Files:**
- Create: `Assets/SkySpaceAgency/Code/KSPService/VabReader.cs`

**Interfaces:**
- Consumes: the DTOs of Tasks 2 to 4, `VabJson.Parse`.
- Produces, in namespace `K2D2.KSPService`:
  - `VabReader.InVab()`, `Builder() : ObjectAssemblyBuilder` (null unless loaded), `MainAssembly(builder) : IObjectAssembly` (null when there is none), `VesselName(builder)`;
  - `GameAssembly(builder, main) : JToken`, `ReadVessel(builder, main) : VabVessel`, `ReadAllParts(builder) : List<VabDumpPart>`;
  - `ReadCampaign() : VabCampaign`, `ExportMeta(DateTime) : VabExportMeta`;
  - `KnownPartNames(builder) : HashSet<string>`, `CapacityLookup(builder) : Func<string, string, double?>`.

No unit test: the class reads game objects. The test command is the compile check, and in-game tests 1 and 5 check the behavior.

- [ ] **Step 1: Write the class**

`Assets/SkySpaceAgency/Code/KSPService/VabReader.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.VabTools;
using KSP.Game;
using KSP.Modules;
using KSP.OAB;
using KSP.Sim.Definitions;
using KSP.Sim.DeltaV;
using KSP.Sim.impl;
using KTools;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace K2D2.KSPService
{
    // Reads the open VAB into the plain structures of K2D2.VabTools. Thin on purpose: rounding,
    // aggregation and every check live in the pure classes, tested in the editor.
    public static class VabReader
    {
        public static bool InVab()
        {
            var game = GeneralTools.Game;
            if (game == null)
                return false;
            // Same access as K2D2_Plugin.ValidScene.
            var gameState = game.GlobalGameState?.GetGameState();
            return gameState != null && gameState.GameState == GameState.VehicleAssemblyBuilder;
        }

        public static ObjectAssemblyBuilder Builder()
        {
            ObjectAssemblyBuilder builder = GeneralTools.Game?.OAB?.Current;
            return builder != null && builder.IsLoaded && !builder.IsShuttingDown ? builder : null;
        }

        public static IObjectAssembly MainAssembly(ObjectAssemblyBuilder builder)
        {
            if (builder == null)
                return null;
            OABSessionInformation stats = builder.Stats;
            return stats != null && stats.HasMainAssembly ? stats.MainAssembly : null;
        }

        public static string VesselName(ObjectAssemblyBuilder builder)
        {
            try { return builder.Stats.GetCurrentWorkspaceVehicleDisplayName; }
            catch (Exception) { return null; }
        }

        // The game's own JSON of the assembly (SerializedAssembly), parsed losslessly.
        public static JToken GameAssembly(ObjectAssemblyBuilder builder, IObjectAssembly main) =>
            VabJson.Parse(builder.UtilFileIO.AssemblyToString(main));

        public static VabVessel ReadVessel(ObjectAssemblyBuilder builder, IObjectAssembly main)
        {
            var vessel = new VabVessel
            {
                Name = VesselName(builder),
                MassWetT = main.GetWetMass(),
                MassDryT = main.GetDryMass(),
            };
            VesselDeltaVComponent dv = main.VesselDeltaV;
            if (dv != null)
            {
                vessel.DeltaVReady = dv.IsReady;
                vessel.DeltaVTotalVac = dv.TotalDeltaVVac;
                vessel.DeltaVTotalActual = dv.TotalDeltaVActual;
                if (dv.StageInfo != null)
                    foreach (DeltaVStageInfo s in dv.StageInfo)
                        if (s != null)
                            vessel.Stages.Add(ReadStage(s));
            }
            GameInstance game = GeneralTools.Game;
            foreach (IObjectAssemblyPart part in main.Parts)
                if (part != null)
                    vessel.Parts.Add(ReadPart(game, part));
            return vessel;
        }

        static string Id(IGGuid id) => id.Guid.ToString("D");

        static VabStage ReadStage(DeltaVStageInfo s)
        {
            var stage = new VabStage
            {
                Stage = s.Stage,
                StartMassT = s.StartMass,
                EndMassT = s.EndMass,
                DryMassT = s.DryMass,
                FuelMassT = s.FuelMass,
                IspVac = s.IspVac,
                IspAsl = s.IspASL,
                ThrustVacKN = s.ThrustVac,
                ThrustAslKN = s.ThrustASL,
                TwrVac = s.TWRVac,
                TwrAsl = s.TWRASL,
                TwrActual = s.TWRActual,
                DeltaVVac = s.DeltaVinVac,
                DeltaVAsl = s.DeltaVatASL,
                DeltaVActual = s.DeltaVActual,
                BurnTimeS = s.StageBurnTime,
            };
            var situation = s.DeltaVSituation;   // KSP.Sim.State.DeltaVSituation (struct)
            stage.SituationBody = situation.CelestialBody;
            stage.SituationAtmosphere = situation.IsAtmosphere;
            stage.SituationAltitudeM = situation.Altitude;
            if (s.Parts != null)
                foreach (DeltaVPartInfo p in s.Parts)
                    if (p?.PartRef != null)
                        stage.PartGuids.Add(Id(p.PartRef.GlobalId));
            if (s.EnginesActiveInStage != null)
                foreach (DeltaVEngineInfo e in s.EnginesActiveInStage)
                    if (e?.Part != null)
                        stage.EngineGuids.Add(Id(e.Part.GlobalId));
            return stage;
        }

        static VabPart ReadPart(GameInstance game, IObjectAssemblyPart part)
        {
            IObjectAssemblyAvailablePart available = part.AvailablePart;
            IObjectAssemblyPart parent = part.ParentPart;
            var vp = new VabPart
            {
                Guid = Id(part.UniqueId),
                Name = available?.Name,
                Title = available?.Title,
                ParentGuid = parent != null ? Id(parent.UniqueId) : null,
                AttachNode = parent != null ? part.MyNodeConnectedToParent?.NodeTag : null,
                SymmetryGroup = part.SymmetrySet?.ID,
                DryMassT = available?.Mass ?? 0f,
                Cost = available?.PartData?.cost ?? 0,
            };
            if (part.Resources != null)
                foreach (IObjectAssemblyResource r in part.Resources)
                    if (r != null)
                        vp.Resources.Add(new VabResource { Name = r.Name, Amount = r.Count, Capacity = r.Capacity, MassT = r.TotalMass });
            vp.Modules.AddRange(ModuleNames(game, vp.Name));
            return vp;
        }

        static IEnumerable<string> ModuleNames(GameInstance game, string partName)
        {
            if (partName == null || game?.Parts == null || !game.Parts.TryGet(partName, out PartCore core) ||
                core?.modules == null)
                return Enumerable.Empty<string>();
            return core.modules.Where(m => m != null).Select(m => m.GetType().Name).ToList();
        }

        public static HashSet<string> KnownPartNames(ObjectAssemblyBuilder builder)
        {
            var known = new HashSet<string>();
            if (builder.Stats.AllKnownParts != null)
                foreach (IObjectAssemblyAvailablePart p in builder.Stats.AllKnownParts)
                    if (p?.Name != null)
                        known.Add(p.Name);
            GameInstance game = GeneralTools.Game;
            if (game?.Parts?.PartData != null)
                foreach (string name in game.Parts.PartData.Keys)
                    known.Add(name);
            return known;
        }

        // Capacity of each resource container in the part definitions known to the VAB.
        public static Func<string, string, double?> CapacityLookup(ObjectAssemblyBuilder builder)
        {
            var map = new Dictionary<string, Dictionary<string, double>>();
            if (builder.Stats.AllKnownParts != null)
                foreach (IObjectAssemblyAvailablePart p in builder.Stats.AllKnownParts)
                {
                    PartData data = p?.PartData;
                    if (p?.Name == null || data?.resourceContainers == null)
                        continue;
                    var inner = new Dictionary<string, double>();
                    foreach (var container in data.resourceContainers)
                        if (container?.name != null)
                            inner[container.name] = container.capacityUnits;
                    map[p.Name] = inner;
                }
            return (part, resource) =>
                part != null && resource != null && map.TryGetValue(part, out var inner) &&
                inner.TryGetValue(resource, out double capacity)
                    ? capacity
                    : (double?)null;
        }

        public static List<VabDumpPart> ReadAllParts(ObjectAssemblyBuilder builder)
        {
            GameInstance game = GeneralTools.Game;
            var available = new HashSet<string>(builder.Stats.AvailableParts.Where(p => p?.Name != null).Select(p => p.Name));
            IReadOnlyDictionary<string, string> techOf = null;
            try { techOf = game.ScienceManager?.TechNodeDataStore?.PartIDLookup; }
            catch (Exception) { }

            var list = new List<VabDumpPart>();
            foreach (PartCore core in game.Parts.AllParts())
            {
                PartData d = core?.data;
                if (d?.partName == null)
                    continue;
                string name = d.partName;
                var part = new VabDumpPart
                {
                    Name = name,
                    Title = d.Title,
                    Category = d.category.ToString(),
                    Family = d.family,
                    Size = d.sizeCategory.ToString(),
                    Unlocked = available.Contains(name),
                    CampaignUnlocked = CampaignUnlocked(game, name),
                    Cost = d.cost,
                    MassT = d.mass,
                    CrewCapacity = d.crewCapacity,
                    MaxTemp = d.maxTemp,
                    CrashTolerance = d.crashTolerance,
                    RawJson = game.Parts.GetRawJson(name),
                };
                if (techOf != null && techOf.TryGetValue(name, out string node))
                    part.TechNode = node;
                if (d.attachNodes != null)
                    foreach (AttachNodeDefinition n in d.attachNodes)
                        part.AttachNodes.Add(n.nodeID);
                if (d.resourceContainers != null)
                    foreach (var container in d.resourceContainers)
                        if (container != null)
                            part.Resources.Add(new VabResource { Name = container.name, Capacity = container.capacityUnits });
                if (core.modules != null)
                {
                    foreach (ModuleData m in core.modules)
                        if (m != null)
                            part.Modules.Add(m.GetType().Name);
                    part.Engine = ReadEngine(core.modules.OfType<Data_Engine>().FirstOrDefault());
                }
                list.Add(part);
            }
            return list;
        }

        static bool? CampaignUnlocked(GameInstance game, string name)
        {
            try { return game.SessionManager?.IsPartUnlocked(name); }
            catch (Exception) { return null; }
        }

        // Definition values of the current engine mode. Isp = atmosphereCurve at 0 and 1 atm (not
        // verified to be seconds, see the plan); ASL thrust derived as maxThrust * IspAsl / IspVac.
        static VabEngineInfo ReadEngine(Data_Engine engine)
        {
            if (engine?.engineModes == null || engine.engineModes.Length == 0)
                return null;
            int index = Mathf.Clamp(engine.currentEngineModeIndex, 0, engine.engineModes.Length - 1);
            var mode = engine.engineModes[index];
            if (mode == null)
                return null;
            var info = new VabEngineInfo { ThrustVacKN = mode.maxThrust, IspVac = double.NaN, IspAsl = double.NaN };
            if (mode.atmosphereCurve != null)
            {
                info.IspVac = mode.atmosphereCurve.Evaluate(0f);
                info.IspAsl = mode.atmosphereCurve.Evaluate(1f);
            }
            info.ThrustAslKN = info.IspVac > 0 ? mode.maxThrust * info.IspAsl / info.IspVac : double.NaN;
            string mixture = mode.propellant?.mixtureName;
            if (!string.IsNullOrEmpty(mixture))
                info.Propellants.Add(mixture);
            return info;
        }

        public static VabCampaign ReadCampaign()
        {
            GameInstance game = GeneralTools.Game;
            var campaign = new VabCampaign();
            try { campaign.Name = game?.SessionManager?.ActiveCampaignName; } catch (Exception) { }
            try { campaign.Mode = GameManager.Instance?.GameModeManager?.GetCurrentGameModeId(); } catch (Exception) { }
            try { campaign.UnlockAllParts = game != null && game.CheatSystem != null && game.CheatSystem.GetUnlockAllParts(); } catch (Exception) { }
            try
            {
                HashSet<string> nodes = game?.SessionManager?.GetMyAgencyUnlockedTechNodes();
                if (nodes != null)
                    campaign.UnlockedTechNodes = nodes.OrderBy(n => n, StringComparer.Ordinal).ToList();
            }
            catch (Exception) { }
            return campaign;
        }

        public static VabExportMeta ExportMeta(DateTime now) => new VabExportMeta
        {
            ExportedAt = now,
            // Not VersionID.VERSION_TEXT: a const, inlined into this DLL at build time.
            GameVersion = Application.version,
            ModVersion = ModVersion(),
            Campaign = ReadCampaign(),
        };

        static string ModVersion()
        {
            try { return K2D2_Plugin.Instance.SWMetadata.SWInfo.Version; }
            catch (Exception) { return null; }
        }
    }
}
```

- [ ] **Step 2: Compile**

Run the EditMode test command. Expected: 0 `error CS`, 176 tests green.

If a line does not compile, apply the fix for its case:
- `DeltaVSituation` or `AttachNodeDefinition` does not resolve: both are only used through `var` or `foreach`; add `using KSP.Sim.State;`.
- `game.Parts.TryGet` or `GetRawJson` is reported missing: `PartProvider` is in `KSP.Game`, so check the using.
- `available?.PartData?.cost` is rejected: `PartData` is a class (confirmed), so the error comes from `IObjectAssemblyAvailablePart.PartData`. Write `available != null && available.PartData != null ? available.PartData.cost : 0`.
- `GameManager.Instance?.GameModeManager` is rejected: `GameManager` is a MonoBehaviour, so write `GameManager.Instance != null ? GameManager.Instance.GameModeManager?.GetCurrentGameModeId() : null`.
- `container` in `data.resourceContainers` is a struct (the docs show a class), so `container?.name` and `container != null` are rejected: drop both null checks.

- [ ] **Step 3: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/KSPService/VabReader.cs*
git status --short   # only these paths
git commit -m "Read the open VAB rocket, the part catalog and the campaign for the VAB tools

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 8: `VabLoader` and `VabActions` (files, import pipeline, log)

**Files:**
- Create: `Assets/SkySpaceAgency/Code/KSPService/VabLoader.cs`, `Assets/SkySpaceAgency/Code/KSPService/VabActions.cs`
- Modify: `Assets/SkySpaceAgency/Code/K2D2_Plugin.cs` (the `VabFolder` property)

**Interfaces:**
- Consumes: Tasks 1 to 7.
- Produces:
  - `K2D2_Plugin.VabFolder` (internal static), equal to `<AssemblyFolder>/vab`;
  - `VabLoader.SettleTimeout` (3 s), `CurrentSnapshot(builder) : JObject`, `ToSnapshot(JObject)`, `ToAssembly(JObject)` (both throw when the game reads another part count);
  - `VabLoader.ReadAssemblies(VabImportFile) : List<SerializedAssembly>`, `AllUnlockedForGame(...)`;
  - `VabLoader.ApplyWorkspace(builder, JObject snapshot)` (method A), `LoadAsMain(builder, JObject assembly)` (method B);
  - `VabLoader.Settled(builder, expectedMain, expectedTotal)`, `Describe(builder)`;
  - `VabActions.Folder()`, `EnsureFolders()`, `ImportChoices() : List<string>`;
  - `VabActions.Export(out string message) : string`, `Dump(out string message) : string`, `Import(string path, Action<string> report) : IEnumerator`.

No unit test: everything here calls the game or the file system. The rules it applies are tested in Tasks 1 to 6. In-game tests 1 to 5 check the behavior.

- [ ] **Step 1: `VabFolder` in `K2D2_Plugin`**

In `Assets/SkySpaceAgency/Code/K2D2_Plugin.cs`, replace (lines 57-59):

```csharp
        private static string _vacLandingSettingsPath;
        private static string VacLandingSettingsPath =>
            _vacLandingSettingsPath ??= Path.Combine(AssemblyFolder, "skyspaceagency_landing_vac.json");
```

with:

```csharp
        private static string _vacLandingSettingsPath;
        private static string VacLandingSettingsPath =>
            _vacLandingSettingsPath ??= Path.Combine(AssemblyFolder, "skyspaceagency_landing_vac.json");

        // VAB tools files (exports/, imports/, dumps/), next to the DLL like the settings files above.
        internal static string VabFolder => Path.Combine(AssemblyFolder, "vab");
```

- [ ] **Step 2: The loader**

`Assets/SkySpaceAgency/Code/KSPService/VabLoader.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using K2D2.VabTools;
using KSP.IO;
using KSP.OAB;
using KSP.Sim;
using KTools;
using Newtonsoft.Json.Linq;

namespace K2D2.KSPService
{
    // The game calls of the import (see the plan's loader table). Every deserialization is checked
    // against the JSON's part count, so a silently misread file can never clear the workspace.
    public static class VabLoader
    {
        public const float SettleTimeout = 3f;   // s of real time for the VAB to show the loaded rocket

        public static JObject CurrentSnapshot(ObjectAssemblyBuilder builder)
        {
            OABHistoricalSnapshot snapshot = builder.Historian.GenerateSnapshot(OABActionType.None);
            return (JObject)VabJson.Parse(IOProvider.ToJson(snapshot));
        }

        public static OABHistoricalSnapshot ToSnapshot(JObject json)
        {
            var snapshot = IOProvider.FromJson<OABHistoricalSnapshot>(VabJson.WriteCompact(json));
            int expected = VabSnapshot.TotalPartCount(json);
            int read = snapshot?.Assemblies?.Sum(a => a?.Assembly?.parts?.Count ?? 0) ?? -1;
            if (read != expected)
                throw new InvalidOperationException($"the game read {read} of {expected} part(s) of the workspace");
            return snapshot;
        }

        public static SerializedAssembly ToAssembly(JObject json)
        {
            var assembly = IOProvider.FromJson<SerializedAssembly>(VabJson.WriteCompact(json));
            int expected = VabSnapshot.PartCount(json);
            int read = assembly?.parts?.Count ?? -1;
            if (read != expected)
                throw new InvalidOperationException($"the game read {read} of {expected} part(s) of the assembly");
            return assembly;
        }

        // Reads every assembly (and the whole workspace for a workspace file) the way the game will,
        // before anything changes.
        public static List<SerializedAssembly> ReadAssemblies(VabImportFile file)
        {
            List<SerializedAssembly> list = file.Assemblies.Select(a => ToAssembly(a)).ToList();
            if (file.Format == VabImportFormat.WorkspaceSnapshot)
                ToSnapshot(file.Root);
            return list;
        }

        // The game's own unlock check ("true if all parts are unlocked or the science-unlock feature is disabled").
        public static bool AllUnlockedForGame(IEnumerable<SerializedAssembly> assemblies)
        {
            var oab = GeneralTools.Game?.OAB;
            return oab == null || assemblies.All(a => oab.AreAllAssemblyPartsUnlocked(a));
        }

        // Method A: the whole workspace from a snapshot. Deserialized first, so a bad snapshot throws
        // before ClearAll.
        public static void ApplyWorkspace(ObjectAssemblyBuilder builder, JObject snapshotJson)
        {
            OABHistoricalSnapshot snapshot = ToSnapshot(snapshotJson);
            builder.ActivePartTracker.ClearAll();
            builder.UtilFileIO.ApplySnapshot(builder.ActivePartTracker, snapshot, true);
        }

        // Method B: delete the main rocket, then load the assembly as the main one. Deleting first
        // keeps the two copies from sharing GUIDs when a rocket is re-imported.
        public static void LoadAsMain(ObjectAssemblyBuilder builder, JObject assemblyJson)
        {
            SerializedAssembly assembly = ToAssembly(assemblyJson);
            IObjectAssembly oldMain = VabReader.MainAssembly(builder);
            if (oldMain?.Anchor != null)
                builder.ActivePartTracker.DeletePart(oldMain.Anchor);
            builder.UtilFileIO.LoadSerializedAssembly(assembly);
        }

        // Main rocket with expectedMainParts parts, or (expectedMainParts <= 0) expectedTotalParts in the workspace.
        public static bool Settled(ObjectAssemblyBuilder builder, int expectedMainParts, int expectedTotalParts)
        {
            if (builder == null)
                return false;
            if (expectedMainParts > 0)
            {
                IObjectAssembly main = VabReader.MainAssembly(builder);
                return main?.Parts != null && main.Parts.Count == expectedMainParts;
            }
            return builder.ActivePartTracker.PartsMasterList.Count == expectedTotalParts;
        }

        public static string Describe(ObjectAssemblyBuilder builder)
        {
            if (builder == null)
                return "the VAB is closed";
            IObjectAssembly main = VabReader.MainAssembly(builder);
            ObjectAssemblyPartTracker tracker = builder.ActivePartTracker;
            return $"main {main?.Parts?.Count ?? 0} part(s), workspace {tracker.PartsMasterList.Count} part(s) " +
                   $"in {tracker.PartAssemblyCount()} assembly(ies)";
        }
    }
}
```

- [ ] **Step 3: The actions**

`Assets/SkySpaceAgency/Code/KSPService/VabActions.cs`:

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using K2D2.VabTools;
using KSP.OAB;
using KSP.Sim;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace K2D2.KSPService
{
    // The three VAB tools: files, checks, game calls and their "[VabTools] ..." log lines.
    // Called by UI/VabToolsWindow.
    public static class VabActions
    {
        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        const string Unchanged = " Your rocket was not changed.";

        public static string Folder() => K2D2_Plugin.VabFolder;

        static string Folder(string sub) => Path.Combine(Folder(), sub);

        public static void EnsureFolders()
        {
            Directory.CreateDirectory(Folder(VabFiles.ExportsFolder));
            Directory.CreateDirectory(Folder(VabFiles.ImportsFolder));
            Directory.CreateDirectory(Folder(VabFiles.DumpsFolder));
        }

        public static List<string> ImportChoices()
        {
            EnsureFolders();
            return VabFiles.ImportChoices(FileNames(VabFiles.ImportsFolder), FileNames(VabFiles.ExportsFolder));
        }

        static IEnumerable<string> FileNames(string sub) =>
            Directory.GetFiles(Folder(sub), "*.json").Select(f => Path.GetFileName(f));

        static void Log(string message) => K2D2_Plugin.logger.LogInfo("[VabTools] " + message);

        static void WriteJson(string path, JToken document) =>
            File.WriteAllText(path, VabJson.Write(document), Utf8NoBom);

        // ---- Export -------------------------------------------------------------------------

        public static string Export(out string message)
        {
            ObjectAssemblyBuilder builder = VabReader.Builder();
            if (builder == null) { message = "The VAB is not ready yet."; return null; }
            IObjectAssembly main = VabReader.MainAssembly(builder);
            if (main == null) { message = "No rocket in the VAB: nothing to export."; return null; }
            try
            {
                EnsureFolders();
                DateTime now = DateTime.Now;
                JToken assembly = VabReader.GameAssembly(builder, main);
                VabVessel vessel = VabReader.ReadVessel(builder, main);
                JObject document = VabExport.Build(VabReader.ExportMeta(now), VabSummary.Build(vessel), assembly);
                string path = VabFiles.UniquePath(Folder(VabFiles.ExportsFolder),
                    VabFiles.ExportFileName(vessel.Name, now), File.Exists);
                WriteJson(path, document);
                Log($"export: {vessel.Parts.Count} part(s), {vessel.Stages.Count} stage(s) in game order " +
                    $"[{string.Join(", ", vessel.Stages.Select(s => s.Stage))}], delta-v ready {vessel.DeltaVReady}, " +
                    $"total vac {VabNum.Text(vessel.DeltaVTotalVac)} m/s -> {path}");
                message = "Exported to " + path +
                          (vessel.DeltaVReady ? "" : ". Delta-v was not ready yet: export again in a moment.");
                return path;
            }
            catch (Exception e)
            {
                Log("export failed: " + e);
                message = "Export failed: " + e.Message;
                return null;
            }
        }

        // ---- Dump ---------------------------------------------------------------------------

        public static string Dump(out string message)
        {
            ObjectAssemblyBuilder builder = VabReader.Builder();
            if (builder == null) { message = "The VAB is not ready yet."; return null; }
            try
            {
                EnsureFolders();
                DateTime now = DateTime.Now;
                List<VabDumpPart> parts = VabReader.ReadAllParts(builder);
                IObjectAssembly main = VabReader.MainAssembly(builder);
                JToken example = main != null ? VabReader.GameAssembly(builder, main) : null;
                VabExportMeta meta = VabReader.ExportMeta(now);
                JObject document = VabDump.Build(meta.Campaign, now, meta.GameVersion, meta.ModVersion, parts, example, false);
                string text = VabJson.Write(document);
                string path = VabFiles.UniquePath(Folder(VabFiles.DumpsFolder),
                    VabFiles.DumpFileName(meta.Campaign.Name, now), File.Exists);
                File.WriteAllText(path, text, Utf8NoBom);
                int unlocked = parts.Count(p => p.Unlocked);
                // Spec: part_templates may be limited to unlocked parts if too heavy; measured here.
                int templatesKb = VabJson.WriteCompact(document["part_templates"]).Length / 1024;
                Log($"dump: {parts.Count} part(s), {unlocked} unlocked in this VAB, " +
                    $"{parts.Count(p => p.Engine != null)} with engine data, {parts.Count(p => p.TechNode != null)} with a tech node, " +
                    $"templates ~{templatesKb} KB compact, file ~{text.Length / 1024} KB, " +
                    $"example assembly {(example != null ? "yes" : "no")} -> {path}");
                message = $"Dumped {parts.Count} parts ({unlocked} unlocked) to {path}";
                return path;
            }
            catch (Exception e)
            {
                Log("dump failed: " + e);
                message = "Dump failed: " + e.Message;
                return null;
            }
        }

        // ---- Import -------------------------------------------------------------------------

        class Attempt
        {
            public string Name;
            public Action<ObjectAssemblyBuilder> Run;
            public int ExpectedMain;
            public int ExpectedTotal;
        }

        class SettleResult
        {
            public bool Settled;
        }

        static string TryRun(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception e)
            {
                Log("import: exception: " + e);
                return e.GetType().Name + ": " + e.Message;
            }
        }

        static IEnumerator WaitSettled(ObjectAssemblyBuilder builder, int expectedMain, int expectedTotal, SettleResult result)
        {
            float deadline = Time.realtimeSinceStartup + VabLoader.SettleTimeout;
            while (true)
            {
                yield return null;
                if (builder == null)
                    yield break;
                if (VabLoader.Settled(builder, expectedMain, expectedTotal))
                {
                    result.Settled = true;
                    yield break;
                }
                if (Time.realtimeSinceStartup > deadline)
                    yield break;
            }
        }

        static int HistoryCount(ObjectAssemblyBuilder builder)
        {
            try { return builder.Historian?.History?.Count ?? -1; }
            catch (Exception) { return -1; }
        }

        // The loader table of the plan.
        static List<Attempt> Plan(VabImportFile file, JObject original)
        {
            var attempts = new List<Attempt>();
            if (file.Format == VabImportFormat.WorkspaceSnapshot)
            {
                attempts.Add(new Attempt
                {
                    Name = "A (whole workspace from the file: ClearAll + ApplySnapshot)",
                    Run = b => VabLoader.ApplyWorkspace(b, file.Root),
                    ExpectedMain = VabSnapshot.MainPartCount(file.Root),
                    ExpectedTotal = VabSnapshot.TotalPartCount(file.Root),
                });
                return attempts;
            }
            int imported = VabSnapshot.PartCount(file.Assembly);
            var swapped = (JObject)original.DeepClone();
            if (VabSnapshot.ReplaceMain(swapped, file.Assembly))
                attempts.Add(new Attempt
                {
                    Name = "A (main rocket swapped in the workspace snapshot: ClearAll + ApplySnapshot)",
                    Run = b => VabLoader.ApplyWorkspace(b, swapped),
                    ExpectedMain = imported,
                    ExpectedTotal = VabSnapshot.TotalPartCount(swapped),
                });
            attempts.Add(new Attempt
            {
                Name = "B (main rocket deleted, then LoadSerializedAssembly)",
                Run = b => VabLoader.LoadAsMain(b, file.Assembly),
                ExpectedMain = imported,
                ExpectedTotal = VabSnapshot.TotalPartCount(original) - VabSnapshot.MainPartCount(original) + imported,
            });
            return attempts;
        }

        static string SuccessMessage(VabImportFile file, List<string> clamps, string backupPath)
        {
            string what = file.Format == VabImportFormat.WorkspaceSnapshot
                ? "Workspace loaded from the file"
                : $"Imported as the main rocket ({VabSnapshot.PartCount(file.Assembly)} parts)";
            string clampNote = clamps.Count == 0
                ? ""
                : $" {clamps.Count} resource value(s) lowered to capacity, first: {clamps[0]}.";
            return $"{what}. Previous workspace saved to exports/{Path.GetFileName(backupPath)}.{clampNote}";
        }

        public static IEnumerator Import(string path, Action<string> report)
        {
            ObjectAssemblyBuilder builder = VabReader.Builder();
            if (builder == null) { report("The VAB is not ready yet."); yield break; }
            string fileName = Path.GetFileName(path);

            // 1. Read and recognize the file.
            string text = null;
            string error = TryRun(() => text = File.ReadAllText(path, Encoding.UTF8));
            if (error != null)
            {
                Log($"import: cannot read {path}: {error}");
                report($"Cannot read {fileName}: {error}");
                yield break;
            }
            VabImportResult parsed = VabImport.Parse(text);
            if (!parsed.Ok)
            {
                Log($"import: {fileName}: refused: {parsed.Error}");
                report("Refused: " + parsed.Error + Unchanged);
                yield break;
            }
            VabImportFile file = parsed.File;
            Log($"import: {fileName}: recognized as {file.Format}, {file.Assemblies.Count} assembly(ies), " +
                $"{file.Assemblies.Sum(a => VabSnapshot.PartCount(a))} part(s)");

            // 2. Structure: GUIDs, root, node pairs.
            List<string> problems = file.Assemblies.SelectMany(a => VabImport.CheckStructure(a)).ToList();
            Log($"import: structure check: {problems.Count} problem(s)" + (problems.Count > 0 ? ": " + string.Join(" | ", problems) : ""));
            if (problems.Count > 0)
            {
                report("Refused: " + problems[0] +
                       (problems.Count > 1 ? $" (and {problems.Count - 1} more, see the log)." : "") + Unchanged);
                yield break;
            }

            // 3. Parts: every part must be offered by this VAB (spec: refuse and list them).
            List<string> names = VabImport.PartNames(file.Assemblies);
            var available = new HashSet<string>(builder.Stats.AvailableParts.Where(p => p?.Name != null).Select(p => p.Name));
            List<VabMissingPart> missing = VabImport.MissingParts(names, available, VabReader.KnownPartNames(builder));
            Log($"import: parts checked: {names.Count} distinct name(s), {available.Count} available in this VAB, " +
                $"{missing.Count} missing" +
                (missing.Count > 0 ? ": " + string.Join(", ", missing.Select(m => m.Name + (m.Known ? " (not unlocked)" : " (unknown)"))) : ""));
            if (missing.Count > 0)
            {
                report(VabImport.DescribeMissing(missing));
                yield break;
            }

            // 4. Resource amounts above capacity are lowered (spec, choix 2).
            Func<string, string, double?> capacity = VabReader.CapacityLookup(builder);
            List<string> clamps = file.Assemblies.SelectMany(a => VabImport.ClampResources(a, capacity)).ToList();
            Log($"import: resources: {clamps.Count} value(s) lowered");
            foreach (string c in clamps)
                Log("import: clamped " + c);

            // 5. The game must read the JSON and agree that every part is unlocked.
            List<SerializedAssembly> readable = null;
            error = TryRun(() => readable = VabLoader.ReadAssemblies(file));
            if (error != null)
            {
                Log("import: the game cannot read the file: " + error);
                report("Refused: the game cannot read this assembly (" + error + ")." + Unchanged);
                yield break;
            }
            bool unlocked = true, enough = true;
            string unlockError = TryRun(() => unlocked = VabLoader.AllUnlockedForGame(readable));
            string fundsError = TryRun(() => enough = readable.All(a => builder.HasEnoughResourcesToLoadAssembly(a)));
            Log($"import: game checks: AreAllAssemblyPartsUnlocked {unlockError ?? unlocked.ToString()}, " +
                $"HasEnoughResourcesToLoadAssembly {fundsError ?? enough.ToString()} (not enforced)");
            if (unlockError == null && !unlocked)
            {
                report("Refused: the game reports that this rocket uses parts not unlocked in this campaign." + Unchanged);
                yield break;
            }

            // 6. Backup of the whole workspace (spec, choix 3), before anything changes.
            JObject original = null;
            string backupPath = null;
            error = TryRun(() =>
            {
                original = VabLoader.CurrentSnapshot(builder);
                EnsureFolders();
                backupPath = VabFiles.UniquePath(Folder(VabFiles.ExportsFolder),
                    VabFiles.BackupFileName(VabReader.VesselName(builder), DateTime.Now), File.Exists);
                WriteJson(backupPath, original);
            });
            if (error != null)
            {
                Log("import: backup failed, nothing changed: " + error);
                report("Refused: the backup of the current rocket failed (" + error + ")." + Unchanged);
                yield break;
            }
            int originalMain = VabSnapshot.MainPartCount(original);
            int originalTotal = VabSnapshot.TotalPartCount(original);
            Log($"import: backup of the whole workspace (main {originalMain} part(s), {originalTotal} in all) -> {backupPath}");
            int historyBefore = HistoryCount(builder);

            // 7. Load, method by method; after a failure, restore the original before going on.
            string failure = null;
            foreach (Attempt attempt in Plan(file, original))
            {
                Log($"import: method {attempt.Name}: expecting main {attempt.ExpectedMain} part(s), {attempt.ExpectedTotal} in all");
                string attemptError = TryRun(() => attempt.Run(builder));
                if (attemptError == null)
                {
                    var settle = new SettleResult();
                    yield return WaitSettled(builder, attempt.ExpectedMain, attempt.ExpectedTotal, settle);
                    if (!settle.Settled)
                        attemptError = $"after {VabLoader.SettleTimeout:0} s the VAB shows {VabLoader.Describe(builder)}";
                }
                if (attemptError == null)
                {
                    Log($"import: result: success with method {attempt.Name}; {VabLoader.Describe(builder)}; " +
                        $"undo history {historyBefore} -> {HistoryCount(builder)} snapshot(s)");
                    report(SuccessMessage(file, clamps, backupPath));
                    yield break;
                }

                failure = attemptError;
                Log($"import: method {attempt.Name} failed: {attemptError}");
                string restoreError = TryRun(() => VabLoader.ApplyWorkspace(builder, original));
                if (restoreError == null)
                {
                    var settle = new SettleResult();
                    yield return WaitSettled(builder, originalMain, originalTotal, settle);
                    if (!settle.Settled)
                        restoreError = "the VAB shows " + VabLoader.Describe(builder);
                }
                if (restoreError != null)
                {
                    Log("import: restore failed: " + restoreError);
                    report($"Import failed ({attemptError}) and the previous rocket could not be restored. " +
                           $"Choose exports/{Path.GetFileName(backupPath)} in the list and import it.");
                    yield break;
                }
                Log("import: previous workspace restored: " + VabLoader.Describe(builder));
            }
            Log("import: result: failed: " + failure);
            report($"Import failed: {failure}. Your previous rocket was restored (backup: exports/{Path.GetFileName(backupPath)}).");
        }
    }
}
```

- [ ] **Step 4: Compile**

Run the EditMode test command. Expected: 0 `error CS`, 176 tests green.

If a line does not compile, apply the fix for its case:
- `OABActionType.None` does not resolve: the enum is `KSP.OAB.OABActionType`, so check the using.
- `IOProvider` does not resolve: it is `KSP.IO.IOProvider`.
- `snapshot?.Assemblies?.Sum(...)` is rejected: `OABPlacedAssembly` is a class (confirmed), so the error comes from the lambda. Write it as a `foreach` loop.
- `Historian.History.Count` is rejected: `History` is an `IReadOnlyCollection`, so add `using System.Linq;` and call `.Count()`.

- [ ] **Step 5: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/K2D2_Plugin.cs Assets/SkySpaceAgency/Code/KSPService/VabLoader.cs* Assets/SkySpaceAgency/Code/KSPService/VabActions.cs*
git status --short   # only these paths
git commit -m "Add the VAB export, dump and import pipeline with backup, fallback and restore

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 9: `VabToolsWindow` and the VAB AppBar button

**Files:**
- Create: `Assets/SkySpaceAgency/Code/UI/VabToolsWindow.cs`
- Modify: `Assets/SkySpaceAgency/Code/K2D2_Plugin.cs`

**Interfaces:**
- Consumes: `VabActions`, `VabReader.InVab()`, `DragManipulator`, `K2UI.VisualElementExtension.Show`.
- Produces:
  - `VabToolsWindow.Create(VisualTreeAsset styleSource) : VabToolsWindow`;
  - `IsWindowOpen` (only true in the VAB; it syncs the OAB AppBar toggle `ToolbarOabButtonID`).

No unit test (UI). The test command is the compile check; every in-game test goes through this window.

- [ ] **Step 1: The window**

`Assets/SkySpaceAgency/Code/UI/VabToolsWindow.cs`:

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using K2D2.KSPService;
using K2UI;
using KSP.UI.Binding;
using UitkForKsp2.API;
using UnityEngine;
using UnityEngine.UIElements;

namespace K2D2.UI
{
    /// <summary>
    /// The "SkySpaceAgency — VAB tools" window: export, import, parts dump, open folder, status line.
    /// Built in code, so it needs no Addressables entry of its own (only K2D2_Window.uxml is one):
    /// it borrows that asset's style sheets and AppShell's USS class names. VAB only: it closes
    /// itself in any other scene. The flight window keeps its own scene gating.
    /// </summary>
    public class VabToolsWindow : MonoBehaviour
    {
        public const string WindowId = "SkySpaceAgencyVabTools";
        const string NoFileChoice = "(no .json file in vab/imports or vab/exports)";

        VisualElement _window;
        DropdownField _files;
        Label _status;
        readonly List<Button> _buttons = new();
        bool _isOpen;
        bool _busy;

        public static VabToolsWindow Create(VisualTreeAsset styleSource)
        {
            // Full-screen host that lets clicks through to the VAB; the window sits inside it.
            var host = new VisualElement { name = "vab-tools-host", pickingMode = PickingMode.Ignore };
            host.style.flexGrow = 1;

            var options = new WindowOptions
            {
                WindowId = WindowId,
                Parent = null,
                IsHidingEnabled = true,
                DisableGameInputForTextFields = true,
                MoveOptions = new MoveOptions
                {
                    IsMovingEnabled = false,   // DragManipulator below, as for the main window
                    CheckScreenBounds = true
                }
            };
            var panel = Window.Create(options, host);
            var window = panel.gameObject.AddComponent<VabToolsWindow>();
            window.Build(host, styleSource);
            return window;
        }

        void Build(VisualElement host, VisualTreeAsset styleSource)
        {
            bool styled = false;
            if (styleSource != null)
            {
                // Unity API (VisualTreeAsset.stylesheets): K2D2.uss, KerbalUI.uss, K2UI*.uss.
                foreach (StyleSheet sheet in styleSource.stylesheets)
                {
                    if (sheet == null)
                        continue;
                    host.styleSheets.Add(sheet);
                    styled = true;
                }
            }

            _window = new VisualElement { name = "vab-tools" };
            _window.AddToClassList("root");
            _window.AddToClassList("oab-window-root");
            _window.style.position = Position.Absolute;
            _window.style.left = 420;
            _window.style.top = 90;
            _window.style.width = 340;
            if (!styled)
            {
                _window.style.backgroundColor = new Color(0.07f, 0.08f, 0.11f, 0.95f);
                Pad(_window, 8);
            }

            var header = new VisualElement { name = "header" };
            header.AddToClassList("oab-window-header");
            header.style.flexDirection = FlexDirection.Row;
            header.style.alignItems = Align.Center;
            var title = new Label("SkySpaceAgency — VAB tools") { name = "header-title" };
            title.AddToClassList("oab-window-header-title");
            title.style.flexGrow = 1;
            var close = new Button(() => IsWindowOpen = false) { name = "close-button", text = "x" };
            close.AddToClassList("oab-close-button");
            header.Add(title);
            header.Add(close);
            _window.Add(header);

            var content = new VisualElement { name = "content" };
            content.AddToClassList("oab-window-content");
            _window.Add(content);

            content.Add(SectionLabel("Export"));
            content.Add(NewButton("Export", OnExport, false));

            content.Add(SectionLabel("Import"));
            var fileRow = Row();
            _files = new DropdownField { name = "import-file" };
            _files.style.flexGrow = 1;
            fileRow.Add(_files);
            fileRow.Add(NewButton("Refresh", RefreshFiles, false));
            content.Add(fileRow);
            content.Add(NewButton("Import (replaces the current rocket)", OnImport, false));

            content.Add(SectionLabel("Parts and files"));
            var toolsRow = Row();
            toolsRow.Add(NewButton("Dump parts", OnDump, true));
            toolsRow.Add(NewButton("Open folder", OnOpenFolder, true));
            content.Add(toolsRow);

            _status = new Label("Ready.") { name = "status" };
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.marginTop = 6;
            content.Add(_status);

            host.Add(_window);
            _window.AddManipulator(new DragManipulator(false, "vab_window_pos"));
            _window.Show(false);
        }

        static void Pad(VisualElement e, float px)
        {
            e.style.paddingLeft = px;
            e.style.paddingRight = px;
            e.style.paddingTop = px;
            e.style.paddingBottom = px;
        }

        static Label SectionLabel(string text)
        {
            var label = new Label(text);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.marginTop = 6;
            return label;
        }

        static VisualElement Row()
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            return row;
        }

        Button NewButton(string text, Action onClick, bool grow)
        {
            var button = new Button(onClick) { text = text };
            if (grow)
                button.style.flexGrow = 1;
            _buttons.Add(button);
            return button;
        }

        /// <summary>Opens or closes the window; it only opens in the VAB.</summary>
        public bool IsWindowOpen
        {
            get => _isOpen;
            set
            {
                if (value && !VabReader.InVab())
                    value = false;
                if (_isOpen == value)
                    return;   // also ends the AppBar toggle -> callback -> setter loop
                _isOpen = value;
                _window?.Show(value);
                GameObject.Find(K2D2_Plugin.ToolbarOabButtonID)
                    ?.GetComponent<UIValue_WriteBool_Toggle>()
                    ?.SetValue(value);
                if (value)
                    RefreshFiles();
            }
        }

        void Update()
        {
            if (_isOpen && !VabReader.InVab())
                IsWindowOpen = false;
        }

        void SetStatus(string text)
        {
            if (_status != null)
                _status.text = text;
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            foreach (Button b in _buttons)
                b.SetEnabled(!busy);
        }

        void RefreshFiles()
        {
            if (_files == null)
                return;
            List<string> choices;
            try { choices = VabActions.ImportChoices(); }
            catch (Exception e)
            {
                K2D2_Plugin.logger.LogError("[VabTools] cannot list the import files: " + e.Message);
                choices = new List<string>();
            }
            string previous = _files.value;
            if (choices.Count == 0)
            {
                _files.choices = new List<string> { NoFileChoice };
                _files.SetValueWithoutNotify(NoFileChoice);
                return;
            }
            _files.choices = choices;
            _files.SetValueWithoutNotify(choices.Contains(previous) ? previous : choices[0]);
        }

        void OnExport()
        {
            if (_busy)
                return;
            VabActions.Export(out string message);
            SetStatus(message);
            RefreshFiles();
        }

        void OnDump()
        {
            if (_busy)
                return;
            VabActions.Dump(out string message);
            SetStatus(message);
        }

        void OnImport()
        {
            if (_busy)
                return;
            string choice = _files?.value;
            if (string.IsNullOrEmpty(choice) || choice == NoFileChoice)
            {
                SetStatus("Put a .json file in vab/imports first (Open folder), then press Refresh.");
                return;
            }
            StartCoroutine(RunImport(Path.Combine(VabActions.Folder(), choice)));
        }

        IEnumerator RunImport(string path)
        {
            SetBusy(true);
            SetStatus($"Importing {Path.GetFileName(path)}...");
            yield return VabActions.Import(path, SetStatus);
            SetBusy(false);
            RefreshFiles();
        }

        void OnOpenFolder()
        {
            try
            {
                VabActions.EnsureFolders();
                Application.OpenURL(new Uri(VabActions.Folder()).AbsoluteUri);
                SetStatus("Opened " + VabActions.Folder());
            }
            catch (Exception e)
            {
                SetStatus("Cannot open the folder: " + e.Message);
            }
        }
    }
}
```

- [ ] **Step 2: Create the window and register the VAB AppBar button**

In `Assets/SkySpaceAgency/Code/K2D2_Plugin.cs`, replace (line 76):

```csharp
        K2D2Window main_window = null;
```

with:

```csharp
        K2D2Window main_window = null;

        VabToolsWindow vab_window = null;
```

Then replace (lines 186-192):

```csharp
            // Register OAB AppBar Button
            // Appbar.RegisterOABAppButton(
            //     ModName,
            //     ToolbarOabButtonID,
            //     AssetManager.GetAsset<Texture2D>($"{ModGuid}/images/icon.png"),
            //     isOpen => myFirstWindowController.IsWindowOpen = isOpen
            // );
```

with:

```csharp
            // Register OAB AppBar Button: the VAB tools window (export, import, parts dump). A separate
            // window that only opens in the VAB (VabToolsWindow closes itself elsewhere); the flight
            // window above keeps its own scene gating (validScenes). It borrows the main window's
            // style sheets, so it needs no Addressables entry of its own.
            vab_window = VabToolsWindow.Create(myFirstWindowUxml);
            Appbar.RegisterOABAppButton(
                SWMetadata.Name + " VAB tools",
                ToolbarOabButtonID,
                AssetsLoader.LoadIcon("icon.png"),
                isOpen => vab_window.IsWindowOpen = isOpen
            );
```

`validScenes`, `ValidScene()`, the `Alt-O` shortcut and the main window's close-outside-flight code in `Update()` are left as they are.

- [ ] **Step 3: Compile**

Run the EditMode test command. Expected: 0 `error CS`, 176 tests green.

If `styleSource.stylesheets` is rejected, the Unity API differs on 6000.6. Delete that `foreach` (and its comment) from `Build`: `styled` stays false and the inline background applies. If `Window.Create(options, host)` is ambiguous with `null`-able overloads, it is not: `host` is a `VisualElement`. If `DropdownField.choices` is read-only on this Unity version, write `_files.choices.Clear(); _files.choices.AddRange(...)`.

- [ ] **Step 4: Commit**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add Assets/SkySpaceAgency/Code/UI/VabToolsWindow.cs* Assets/SkySpaceAgency/Code/K2D2_Plugin.cs
git status --short   # only these paths
git commit -m "Add the VAB tools window and its VAB AppBar button

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

---

### Task 10: Build, install, document

The controller does this task itself, not a subagent, because it touches the game folder and the parent repository.

**Files:**
- Modify: `CHANGELOG.md` (section `## Unreleased (SkySpaceAgency)`) and `README.md` (autopilot list and Known limitations)
- Parent repo: `D:/KSPReduxModding/CLAUDE.md` (SkySpaceAgency section) and the submodule pointer

- [ ] **Step 1: Build the zip**

Run the build command. Expected: 0 `error CS`, 0 `logLevel: 2`, and `Deploy/SkySpaceAgency.zip` present (~6.3 MB: no asset was added).

- [ ] **Step 2: Install in the game**

```bash
G="/i/SteamLibrary/steamapps/common/Kerbal Space Program 2/mods/SkySpaceAgency"
S=/c/Users/killi/AppData/Local/Temp/claude/D--KSPReduxModding/a7cbf702-b607-4bb2-8603-776172a2b3f1/scratchpad
rm -rf "$S/zip" && mkdir -p "$S/zip" && unzip -q -o /d/KSPReduxModding/SkySpaceAgency/Deploy/SkySpaceAgency.zip -d "$S/zip"
ls "$S/zip"
```

Copy the unzipped mod folder over `"$G"`. Keep the `skyspaceagency_*.json` settings files and any `vab/` folder already there: a copy deletes nothing. Then compare the size and date of `SkySpaceAgency.dll` in the game folder with the build output.

- [ ] **Step 3: CHANGELOG and README**

In `CHANGELOG.md`, under `## Unreleased (SkySpaceAgency)` → `### Added`, add first:

```markdown
- **VAB tools** (VAB AppBar → "SkySpaceAgency VAB tools"), a small window that only opens in the VAB. Files go to `mods/SkySpaceAgency/vab/`.
  - **Export** writes `vab/exports/<vessel>_<date>.json`, made to be handed to an AI:
    - a readable summary: per stage, Δv (vacuum, sea level, actual), TWR, Isp, thrust, masses and burn time; every part with its parent, stage, symmetry group, resources and modules; the resource totals;
    - instructions for the AI;
    - the game's own JSON of the rocket.
  - **Import** loads a file from `vab/imports/` (or `vab/exports/`) as the main rocket. It accepts a SkySpaceAgency export, the game's assembly JSON, or a VAB workspace file.
    - **Refusals**: before touching anything, it refuses a file with a part this VAB does not offer (the parts are listed), a broken structure, or JSON the game cannot read.
    - **Resources**: amounts above a part's capacity are lowered and reported.
    - **Backup**: the whole workspace is first saved to `vab/exports/before_import_….json`, which can be imported back. If loading fails, the previous rocket is restored.
  - **Dump parts** writes `vab/dumps/parts_<campaign>_<date>.json`: every part, unlocked or not, its tech node, characteristics, engine figures, raw definition, plus an example assembly and instructions for building a rocket from scratch.
  - **Open folder** opens `vab/` in the Explorer.
```

In `README.md`, add to the list of autopilots (after the "Attitude" line):

```markdown
- **VAB tools** - in the VAB: export the rocket to JSON for an AI (summary with Δv per stage), import a corrected rocket (only unlocked parts, with an automatic backup), and dump the parts of the campaign
```

In `README.md` → "Known limitations", add:

```markdown
- VAB tools: the import replaces the main rocket through the game's workspace restore (or, as a fallback, its assembly loader); it is not an undo step - the `before_import_…` backup in `vab/exports/` is the way back. Engine Isp in the parts dump comes from the part definitions and may differ from the VAB's figures.
```

- [ ] **Step 4: Commit and push the mod docs, then bump the parent**

```bash
cd /d/KSPReduxModding/SkySpaceAgency
git add CHANGELOG.md README.md
git commit -m "Document the VAB tools

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

Then, in `D:/KSPReduxModding/CLAUDE.md`, add a **Outils VAB** bullet to the SkySpaceAgency section, after the "Circularisation et onglet Orbit en boucle fermée" bullet. It should:
- give the spec and plan paths (`…/specs/2026-10-16-vab-json-tools-design.md`, `…/plans/2026-10-16-vab-json-tools.md`);
- say that the pure code (`Code/VabTools/`) is tested by `Assets/Tests/Vab`, the first test assembly to reference `Newtonsoft.Json.dll`, and give the total test count read from `results.xml`;
- say that the window is built in code with `Window.Create(options, VisualElement)` because a new UXML is not added to Addressables automatically (only `K2D2_Window.uxml` is an entry), and that files live in `<mod>/vab/{exports,imports,dumps}`;
- say that the import uses method A (main rocket swapped in a workspace snapshot, `ClearAll` + `ApplySnapshot`), then method B (`DeletePart` + `LoadSerializedAssembly`), checks the result and restores the snapshot after a failure.

It should also list what is **not verified in game** at that date, to read in the log:
- whether `ClearAll` + `ApplySnapshot` replaces the rocket and keeps the detached assemblies, and whether the import shows in the undo history (`[VabTools] import: result: … undo history a -> b`);
- whether `IOProvider.ToJson` / `FromJson` round-trip a `SerializedAssembly` and a snapshot (`the game read n of m part(s)` otherwise);
- whether the stage order of `VesselDeltaV.StageInfo` matches the VAB's numbering (`[VabTools] export: … stage(s) in game order [...]`);
- whether `atmosphereCurve` gives Isp in seconds, and whether `PartIDLookup` is keyed by part name (`[VabTools] dump: … with a tech node`);
- the dump size (`templates ~… KB`), to decide whether to limit `part_templates` to unlocked parts.

Then remove the VAB item from the "Demandes en attente" line: only the Landing sub-project 3 remains there.

```bash
cd /d/KSPReduxModding
git add SkySpaceAgency CLAUDE.md
git commit -m "Bump SkySpaceAgency: VAB JSON tools

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
git push
```

- [ ] **Step 5: Hand the in-game checklist to the user**

Give the user the spec's five in-game tests, with what to look at:
1. **Export a three-stage rocket.** In the export, each stage's `delta_v_*` and `twr_*` match the VAB's figures, and the stage numbers follow the VAB's numbering. Check the `[VabTools] export:` line.
2. **Re-import that export as is** (`exports/<file>` in the list). The rocket is the same, and the status line reports no lowered resource value.
3. **Edit `game_assembly`** (remove a part, keeping its node pair and stage lists consistent), put it in `vab/imports/` and import it. The rocket changes, and `exports/before_import_….json` exists. Then import `before_import_…` to get the old rocket back.
4. **Import a file that contains a part not unlocked in the campaign.** The import is refused, the parts are listed, and the rocket is unchanged.
5. **Dump in a campaign.** The parts marked `"unlocked": true` are exactly those the VAB's parts list offers.

Ask them to send back these log lines:
- `[VabTools] export:`
- every `[VabTools] import:` line (format, structure, parts, resources, game checks, backup, method, result)
- `[VabTools] dump:`

---

## Self-review

**Spec coverage:**

| Spec item | Task |
|---|---|
| Export: readable summary + the game's raw JSON, format/version/dates/versions/campaign/instructions | 2, 3, 7, 8 (`Export`) |
| Summary fields: vessel, masses, Δv totals, `delta_v_ready`, stages (all listed fields, situation), parts (all listed fields), `resources_total` | 2 (`VabSummary`), 7 (`ReadVessel`) |
| Stage order follows the VAB, to be verified | decision 5; 8 (export log line); 10 (in-game test 1) |
| Import accepts a mod export (reads `game_assembly`) or a raw `SerializedAssembly` | 5 (`Parse`); a workspace file too (deviation 1) |
| Before touching: parse, check every `partName` against `Stats.AvailableParts`, refuse and list (name + unlocked or not); rocket intact | 5 (`MissingParts`, `DescribeMissing`), 8 (steps 1-3 of `Import`) |
| Backup of the whole workspace (`GenerateSnapshot`) in `exports/before_import_…` just before replacing, re-importable | 1 (`BackupFileName`), 8 (step 6), deviations 1-2 |
| Replace the main rocket; detached assemblies untouched | 6 (`ReplaceMain`), 8 (method A); method B deletes only the main |
| Load error shown with its message; rocket unchanged until the load succeeds, as far as the API allows | 8 (`TryRun` messages, settle check, restore) |
| Loading method chosen in the plan from the docs, with fallback to the other | decision 2, loader table, 8 |
| Resource above capacity lowered to capacity, reported in the status line and the log | 6 (`ClampResources`), 8 (step 4, `SuccessMessage`); deviation 3 |
| Module data and variants not modified | 6: only `storedUnits` and `capacityUnits` are written; variant parts skip the definition check |
| Dump: format, campaign (name, mode, unlock_all_parts, unlocked_tech_nodes), parts (all listed fields), engine or null, `part_templates` (`GetRawJson`) | 4, 7 (`ReadAllParts`, `ReadCampaign`); deviation 5 for `unlocked` |
| `part_templates` maybe limited to unlocked parts, measured on first use | 4 (`templatesUnlockedOnly`), 8 (size in the dump log), 10 (CLAUDE.md note) |
| Dump `instructions_for_ai` and `example_assembly` (null + "export first" when no rocket) | 4 (`Instructions`, `NoExampleNote`), 8 (`Dump`) |
| UI: OAB AppBar button, separate window "SkySpaceAgency — VAB tools", flight window unchanged | 9; decision 1 |
| Window content: Export (+ path), Import list of `imports/*.json` + "Import (replaces the current rocket)", Dump parts (+ path), Open folder, status line | 9; the list also shows `exports/` (deviation 2) |
| English UI | 9 |
| Folders `<game>/mods/SkySpaceAgency/vab/{exports,imports,dumps}`, file names, UTF-8, indented | 1, 8 (`VabFolder`, `WriteJson`) |
| `[VabTools] import: …` log for every step | 8 |
| EditMode tests: summary (resources, stages), import validation (missing parts, both formats, invalid JSON), file names | 1, 2, 5, 6 |
| In-game tests 1-5 | 10 Step 5 |
| Out of scope (mission analysis, rocket generator, part editing, calling an AI): nothing implements them | — |

**Placeholders:** none. Every code step gives the full code, and every replaced block is quoted from commit `311fa6d`.

**Names checked across tasks:**
- Task 1 helpers:
  - `VabJson.Parse`, `Write` and `WriteCompact` are used in Tasks 4, 5, 7 and 8;
  - `Str`, `Num`, `GuidOf` and `ZeroGuid` are used in Tasks 5 and 6;
  - `VabFiles.IsoTime` is used in Tasks 3 and 4;
  - `ExportFileName`, `BackupFileName`, `DumpFileName`, `UniquePath`, `ImportChoices` and the folder constants are used in Task 8.
- `VabNum.Round`, `Finite`, `Text` and `Json` (Task 2) are used in Tasks 3, 4, 6 and 8. `VabSummary.ThrustDigits`, `IspDigits`, `AmountDigits`, `MassDigits` and `CostDigits` are used in Task 4.
- `VabCampaign.ToJson`, `VabExportMeta.Campaign` and `VabExport.Format` (Task 3) are used in Tasks 4, 5, 7 and 8. `VabDump.Format` (Task 4) is used in Task 5.
- Task 5 to 6 names:
  - `VabImportFile.Assembly`, `Assemblies`, `Root` and `Format` are used in Task 8;
  - `VabImport.CheckStructure`, `PartNames`, `MissingParts`, `DescribeMissing` and `ClampResources` are used in Task 8;
  - `VabSnapshot.ReplaceMain`, `PartCount`, `MainPartCount` and `TotalPartCount` (Task 6) are used in Task 8.
- `VabReader.Builder`, `MainAssembly`, `VesselName`, `GameAssembly`, `ReadVessel`, `ReadAllParts`, `ExportMeta`, `KnownPartNames`, `CapacityLookup` and `InVab` (Task 7) are used in Tasks 8 and 9.
- `VabActions.Folder`, `EnsureFolders`, `ImportChoices`, `Export`, `Dump` and `Import` (Task 8) are used in Task 9.
- `K2D2_Plugin.VabFolder` (Task 8 Step 1) is used by `VabActions`. `ToolbarOabButtonID` already exists and is used in Task 9.
- `VabFixtures` (Task 5) is used in Task 6.

**Test count:** 131 at the start; +11 (Task 1), +5 (2), +3 (3), +6 (4), +12 (5), +8 (6) = **176**.

**Order:**
- Tasks 1 to 6 are pure code, each green before the next.
- Task 5 needs `VabExport.Format` and `VabDump.Format`, so it comes after Tasks 3 and 4.
- Task 6 reuses Task 5's fixtures.
- The game code (7, 8) compiles against the finished pure code.
- The window (9) is wired last, so no intermediate build shows a button that calls missing code.

**Checked against the spec's anti-objectives:**
- A silent or half import is ruled out: every refusal happens before the backup. The backup is written before the first game call. Every method is checked after it runs, and a failure re-applies the backup snapshot. When even the restore fails, the status line names the backup to import.
- Unlocked parts only: the import checks the VAB's own `AvailableParts`, then the game's `AreAllAssemblyPartsUnlocked`.
- An export readable by an AI: summary, titles, Δv per stage, instructions.
