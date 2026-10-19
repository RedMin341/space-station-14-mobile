"""Build-only transforms for the pinned, experimental offline app."""
from pathlib import Path
import base64
import json
import struct
import zipfile

root = Path('space-station-14')
engine = root / 'RobustToolbox'
def change(path, old, new):
    text = path.read_text(encoding='utf-8-sig')
    if old not in text:
        raise RuntimeError(f'Pinned source changed: {path}: {old[:80]}')
    path.write_text(text.replace(old, new))

# Server and packaging projects are dependencies of an Android entry point.
for rel in ['Content.Server/Content.Server.csproj', 'Content.Packaging/Content.Packaging.csproj',
            'RobustToolbox/Robust.Server/Robust.Server.csproj',
            'RobustToolbox/Robust.Packaging/Robust.Packaging.csproj']:
    change(root/rel, '>Exe</OutputType>', '>Library</OutputType>')

# Retain packaging APIs used by the server but exclude their desktop CLI entry points.
for rel in ['Content.Packaging/Content.Packaging.csproj', 'RobustToolbox/Robust.Packaging/Robust.Packaging.csproj']:
    change(root/rel, '</Project>', '<ItemGroup><Compile Remove="Program.cs" /></ItemGroup></Project>')

# The public server DB excludes the system provider in desktop Release builds,
# but Android uses the explicitly bundled libsqlite3.so in every configuration.
change(root/'Content.Server.Database/Content.Server.Database.csproj',
       "Condition=\"'$(UseSystemSqlite)' == 'True' and '$(Configuration)' != 'Release'\"",
       "Condition=\"'$(UseSystemSqlite)' == 'True'\"")

client = engine/'Robust.Client/GameController/GameController.cs' 
change(client, '// Fobos distributes two additional modules with its server content.',
       'if (System.Environment.GetEnvironmentVariable("SS14_OFFLINE_SANDBOX") == "1")\n                    return true;\n\n                // Fobos distributes two additional modules with its server content.')
server = engine/'Robust.Server/BaseServer.cs'
old='if (!_modLoader.TryLoadModulesFrom(Options.AssemblyDirectory, resourceManifest.AssemblyPrefix ?? Options.ContentModulePrefix))'
new='''if (OperatingSystem.IsAndroid() && _modLoader is BaseModLoader androidLoader)
            {
                foreach (var name in new[] { "Content.Shared.Database", "Content.Server.Database",
                    "Content.DeadSpace.Interfaces.Shared", "Content.DeadSpace.Interfaces.Server",
                    "Content.Shared", "Content.Server" })
                    androidLoader.RegisterAlreadyLoadedAssembly(System.Reflection.Assembly.Load(name));
            }
            else ''' + old
change(server,old,new)
# Paired startup markers identify the exact unfinished stage on a phone.
# Keep the existing timeout: diagnostics must not disguise a stalled startup.
startup_stages = [
    ('_serializer.Initialize();', 'Сетевая сериализация'),
    ('_loc.Initialize();', 'Менеджер переводов'),
    ('_loc.AddLoadedToStringSerializer(_stringSerializer);', 'Строки переводов'),
    ('_config.SetupNetworking();', 'Сетевые настройки'),
    ('_playerManager.Initialize(MaxPlayers);', 'Менеджер игроков'),
    ('_placement.Initialize();', 'Размещение объектов'),
    ('_viewVariables.Initialize();', 'Просмотр переменных'),
    ('_modLoader.BroadcastRunLevel(ModRunLevel.Init);', 'Инициализация игровых модулей'),
    ('_entityManager.Initialize();', 'Менеджер сущностей'),
    ('_mapManager.Initialize();', 'Менеджер карт'),
    ('_serialization.Initialize();', 'Сериализация игровых данных'),
    ('ProgramShared.FinishCheckBadFileExtensions(checkBadExtensions);', 'Проверка файлов ресурсов'),
    ('_prototype.Initialize();', 'Менеджер прототипов'),
    ('_prototype.LoadDefaultPrototypes();', 'Загрузка прототипов'),
    ('_refMan.Initialize();', 'Ссылки прототипов'),
    ('IoCManager.Resolve<ToolshedManager>().Initialize();', 'Инструменты администратора'),
    ('_consoleHost.Initialize();', 'Консоль'),
    ('_entityManager.Startup();', 'Запуск сущностей'),
    ('_mapManager.Startup();', 'Запуск карт'),
    ('_modLoader.BroadcastRunLevel(ModRunLevel.PostInit);', 'Завершение игровых модулей'),
]
for statement, label in startup_stages:
    change(server, statement,
           f'System.Console.WriteLine("[START] {label}");\n            {statement}\n'
           f'            System.Console.WriteLine("[DONE] {label}");')
# Content.Init loads two large localization bundles. Trace them individually.
localization = root/'Content.Shared/Localizations/ContentLocalizationManager.cs'
for statement, label in [('_loc.LoadCulture(culture);', 'Русские переводы'),
                         ('_loc.LoadCulture(fallbackCulture);', 'Английские переводы')]:
    change(localization, statement,
           f'System.Console.WriteLine("[START] {label}");\n            {statement}\n'
           f'            System.Console.WriteLine("[DONE] {label}");')

# Instrument the expensive reflection loops without removing any types or changing parallelism.
serialization = engine/'Robust.Shared/Serialization/Manager/SerializationManager.cs'
source = serialization.read_text()
needle = 'Parallel.ForEach(_reflectionManager.FindAllTypes(), type =>'
if source.count(needle) != 2:
    raise RuntimeError('Unexpected serialization reflection loops')
source = source.replace(needle, 'TraceTypeInitialization("Поиск описаний данных", _reflectionManager.FindAllTypes(), type =>', 1)
source = source.replace(needle, 'TraceTypeInitialization("Сбор атрибутов типов", _reflectionManager.FindAllTypes(), type =>', 1)
source = source.replace('Parallel.ForEach(registrations, type =>',
                        'TraceTypeInitialization("Создание описаний данных", registrations, type =>')
serialization.write_text(source)
for statement, label in [
    ('InitializeFlagsAndConstants(flagsTypes, constantsTypes);', 'Флаги и константы'),
    ('InitializeTypeSerializers(typeSerializers);', 'Регистрация обработчиков типов'),
]:
    change(serialization, statement,
           f'System.Console.WriteLine("[SERIAL START] {label}");\n            {statement}\n'
           f'            System.Console.WriteLine("[SERIAL DONE] {label}");')
change(serialization, 'foreach (var baseType in implicitDataDef)',
       'System.Console.WriteLine("[SERIAL START] Неявные описания данных");\n            foreach (var baseType in implicitDataDef)')
change(serialization, '            TraceTypeInitialization("Поиск описаний данных",',
       '            System.Console.WriteLine("[SERIAL DONE] Неявные описания данных");\n            TraceTypeInitialization("Поиск описаний данных",')
change(serialization, 'var duplicateErrors = new StringBuilder();',
       'System.Console.WriteLine("[SERIAL START] Проверка описаний данных");\n            var duplicateErrors = new StringBuilder();')
change(serialization, '_initialized = true;',
       'System.Console.WriteLine("[SERIAL DONE] Проверка описаний данных");\n            _initialized = true;')
(engine/'Robust.Shared/Serialization/Manager/SerializationManager.StartupDiagnostics.cs').write_text(
    Path('scripts/SerializationManager.StartupDiagnostics.cs').read_text())

# Track real completed work throughout the previously silent prototype stage.
import runpy
runpy.run_path('scripts/patch-prototype-progress.py')['patch'](engine)
runpy.run_path('scripts/patch-round-progress.py')['patch'](root)

console=engine/'Robust.Server/Console/SystemConsoleManager.cs'
change(console,'Environment.UserInteractive','!OperatingSystem.IsAndroid() && Environment.UserInteractive')
change(console,'public void UpdateInput()\n        {','public void UpdateInput()\n        {\n            if (OperatingSystem.IsAndroid()) return;')

# Desktop shortcuts are also available as touch buttons in this separate app.
overlay=engine/'Robust.Client/UserInterface/Mobile/MobileControlsOverlay.cs'
change(overlay,'private readonly PojavActionButton _throwButton;',
       'private readonly PojavActionButton _throwButton;\n    private readonly PojavActionButton _sandboxButton, _spawnButton, _adminButton;')
change(overlay,'_defaultButtons.AddRange(_allButtons);','''_sandboxButton = CreateButton("ПЕСОЧНИЦА", Keyboard.Key.B, id: "sandbox");
        _spawnButton = CreateButton("ПРЕДМЕТЫ", Keyboard.Key.F5, id: "spawn");
        _adminButton = CreateButton("АДМИН", Keyboard.Key.F7, id: "admin");
        _defaultButtons.AddRange(_allButtons);''')
change(overlay,'var topBtnW = 46f * scale;','''ArrangeChild(_sandboxButton, new Vector2(160f * scale, 50f * scale), new Vector2(68f * scale, 36f * scale), finalSize);
        ArrangeChild(_spawnButton, new Vector2(235f * scale, 50f * scale), new Vector2(68f * scale, 36f * scale), finalSize);
        ArrangeChild(_adminButton, new Vector2(310f * scale, 50f * scale), new Vector2(60f * scale, 36f * scale), finalSize);
        var topBtnW = 46f * scale;''')

# Generate a format-7 16x16 room: one chunk, breathable atmosphere, no station simulation overhead.
entities=[]
def entity(proto,x,y,anchored=False):
    uid=3+sum(len(g['entities']) for g in entities)
    e={'uid':uid,'components':[{'type':'Transform','parent':2,'pos':f'{x},{y}','anchored':anchored}]}
    entities.append({'proto':proto,'entities':[e]})
    return uid
for x in range(16):
    for y in range(16):
        if x in (0,15) or y in (0,15): entity('WallSolid',x+.5,y+.5,True)
for p,x,y,a in [
 ('SpawnPointAnyJob',7.5,7.5,False),('SpawnPointLatejoin',7.5,7.5,False),
 ('Table',4.5,6.5,True),('Table',5.5,6.5,True),
 ('ClosetTool',3.5,10.5,True),('ClosetMaintenance',4.5,10.5,True),('CrateGeneric',6.5,10.5,False),
 ('ChairOfficeDark',4.5,5.5,False),('Crowbar',4.5,6.5,False),('Wrench',4.5,6.5,False),
 ('Screwdriver',4.5,6.5,False),('Multitool',4.5,6.5,False),('ToolboxElectrical',5.5,6.5,False),
 ('FlashlightLantern',8.5,6.5,False),('ClothingBackpack',9.5,6.5,False),('Medkit',9.5,8.5,False),
 ('ToyRubberDuck',10.5,10.5,False),('TrashBananaPeel',11.5,10.5,False),('MobCat',11.5,11.5,False)]:
    entity(p,x,y,a)
# JSON is valid YAML; use the game's actual seven-byte tile serialization.
base=[{'proto':'','entities':[
 {'uid':1,'components':[{'type':'MetaData','name':'Комната Чёрного Котика'}, {'type':'Transform'},
  {'type':'Map','mapPaused':True},{'type':'GridTree'},{'type':'Broadphase'},{'type':'OccluderTree'},
  {'type':'MapAtmosphere','space':False,'mixture':{'volume':2500,'temperature':293.15,'moles':{'Oxygen':21.824,'Nitrogen':82.103}}},
  {'type':'MapLight','ambientLightColor':'#B0B0B0'}]},
 {'uid':2,'components':[{'type':'Transform','parent':1},{'type':'MapGrid','chunks':{'0,0':{'ind':'0,0','version':7,'tiles':base64.b64encode(struct.pack('<iBBB',1,0,0,0)*256).decode()}}},
 {'type':'Broadphase'},{'type':'Physics','bodyType':'Static'}, {'type':'Fixtures','fixtures':{}},
 {'type':'BecomesStation','id':'OfflineRoom'},{'type':'Gravity'},{'type':'GridPathfinding'},
 {'type':'OccluderTree'},{'type':'ImplicitRoof'}]}]}]
room={'meta':{'format':7,'category':'Map','engineVersion':'276.0.0','entityCount':2+len(entities)},
      'maps':[1],'grids':[2],'orphans':[],'nullspace':[],'tilemap':{0:'Space',1:'FloorSteel'},'entities':base+entities}
proto=[{'type':'gameMap','id':'OfflineRoom','mapName':'Комната Чёрного Котика','mapPath':'/Maps/Test/offline-room.yml',
        'minPlayers':0,'maxRandomOffset':0,'randomRotation':False,'stations':{'OfflineRoom':{'stationProto':'OfflineTrainingStation',
        'components':[{'type':'StationNameSetup','mapNameTemplate':'Песочница Чёрного Котика'},{'type':'StationJobs','availableJobs':{'Passenger':[-1,-1]}}]}}}]
proto.append({'type':'entity','id':'OfflineTrainingStation',
              'parent':['BaseStation','BaseStationJobsSpawning','BaseStationRecords','BaseStationNanotrasen'],
              'categories':['HideSpawnMenu'],'components':[{'type':'Transform'}]})
with zipfile.ZipFile('Content.Android/offline.zip','w',zipfile.ZIP_DEFLATED,compresslevel=1) as z:
    for f in sorted((root/'Resources').rglob('*')):
        if not f.is_file(): continue
        name=f.relative_to(root/'Resources').as_posix()
        if name.startswith(('Maps/','Assemblies/')): continue
        z.write(f,name)
    # Content.Server loads these prototypes from their original path.
    z.writestr('Maps/Test/offline-room.yml',json.dumps(room,ensure_ascii=False))
    z.writestr('Prototypes/Maps/offline-room.yml',json.dumps(proto,ensure_ascii=False))
print('Offline room:',room['meta']['entityCount'],'entities; public content only; loopback server.')
