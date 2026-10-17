"""Instrument the pinned prototype loader without changing loading semantics."""
from pathlib import Path


def patch(engine):
    def change(path, old, new):
        text = path.read_text(encoding='utf-8-sig')
        if text.count(old) != 1:
            raise RuntimeError(f'Expected one prototype patch anchor: {path}: {old[:80]}')
        path.write_text(text.replace(old, new))

    yaml = engine / 'Robust.Shared/Prototypes/PrototypeManager.YamlLoad.cs'
    change(yaml, '        _hasEverBeenReloaded = true;\n        var streams',
           '        using var startup = new PrototypeStartupProgress("Чтение и объединение YAML " + path);\n'
           '        _hasEverBeenReloaded = true;\n        var streams')
    change(yaml, '                                    var data = ExtractMapping((MappingDataNode)mapping);',
           '                                    var data = ExtractMapping((MappingDataNode)mapping);\n'
           '                                    startup.Advance("Прочитано: " + file);')
    change(yaml, '                    return (file, extractedList);',
           '                    startup.Advance("Файл прочитан: " + file);\n'
           '                    return (file, extractedList);')
    change(yaml, '                    MergeMapping(mapping, overwrite, changed);',
           '                    MergeMapping(mapping, overwrite, changed);\n'
           '                    startup.Advance("Объединено: " + file);')

    manager = engine / 'Robust.Shared/Prototypes/PrototypeManager.cs'
    change(manager, '        public void ResolveResults()\n        {',
           '        public void ResolveResults()\n        {\n'
           '            using var startup = new PrototypeStartupProgress("Наследование и создание прототипов");')
    change(manager, 'PushKindInheritance(k, v)', 'PushKindInheritance(k, v, startup)')
    change(manager, 'InstantiateKinds(kinds, inheritanceTasks);', 'InstantiateKinds(kinds, inheritanceTasks, startup);')
    change(manager, 'private void InstantiateKinds(KindData[] kinds, Dictionary<Type, Task> inheritanceTasks)',
           'private void InstantiateKinds(KindData[] kinds, Dictionary<Type, Task> inheritanceTasks, PrototypeStartupProgress startup)')
    change(manager, 'InstantiatePrototypes(kinds, results, hooksChannel)', 'InstantiatePrototypes(kinds, results, hooksChannel, startup)')
    change(manager, '                    hooks.AfterDeserialization();',
           '                    hooks.AfterDeserialization();\n'
           '                    startup.Advance("После десериализации: " + hooks.GetType().Name);')
    change(manager, '            Channel<ISerializationHooks> hooks)',
           '            Channel<ISerializationHooks> hooks, PrototypeStartupProgress startup)')
    change(manager, '                        item.Instance = TryReadPrototype(item.KindData.Type, item.Id, item.Mapping, hookCtx);',
           '                        item.Instance = TryReadPrototype(item.KindData.Type, item.Id, item.Mapping, hookCtx);\n'
           '                        startup.Advance("Обработан: " + item.KindData.Type.Name + "/" + item.Id);')
    change(manager, 'private async Task PushKindInheritance(Type kind, KindData data)',
           'private async Task PushKindInheritance(Type kind, KindData data, PrototypeStartupProgress startup)')
    change(manager, '                    // ReSharper disable once AccessToDisposedClosure\n                    countDown.Signal();',
           '                    startup.Advance("Наследование: " + kind.Name + "/" + id);\n'
           '                    // ReSharper disable once AccessToDisposedClosure\n                    countDown.Signal();')
    (engine / 'Robust.Shared/Prototypes/PrototypeStartupProgress.cs').write_text(
        Path('scripts/PrototypeStartupProgress.cs').read_text())
