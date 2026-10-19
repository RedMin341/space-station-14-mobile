"""Instrument the pinned offline round startup without changing its behavior."""
from pathlib import Path


def trace_method(path, signature, statements):
    source = path.read_text(encoding='utf-8-sig')
    if source.count(signature) != 1:
        raise RuntimeError(f'Unexpected method: {path}: {signature}')
    start = source.index(signature)
    opening = source.index('{', start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    body = source[start:end]
    for statement, label in statements:
        if body.count(statement) != 1:
            raise RuntimeError(f'Unexpected statement: {path}: {statement}')
        body = body.replace(statement,
            f'global::System.Console.WriteLine("[START] {label}");\n            {statement}\n'
            f'            global::System.Console.WriteLine("[DONE] {label}");')
    path.write_text(source[:start] + body + source[end:])


def patch(root):
    root = Path(root)
    engine = root / 'RobustToolbox'
    trace_method(engine / 'Robust.Shared/GameObjects/EntityManager.cs', 'public virtual void FlushEntities()', [
        ('BeforeEntityFlush?.Invoke();', 'Очистка: обработчики до удаления'),
        ('AfterEntityFlush?.Invoke();', 'Очистка: обработчики после удаления'),
    ])
    trace_method(engine / 'Robust.Server/GameStates/PvsSystem.DataStorage.cs', 'private void AfterEntityFlush()', [
        ('ClearPvsData();', 'PVS: очистка данных'),
        ('ShrinkDataMemory();', 'PVS: уменьшение памяти'),
        ('ResetDataMemory();', 'PVS: сброс памяти'),
    ])
    trace_method(engine / 'Robust.Server/GameStates/PvsSystem.DataStorage.cs', 'private void ClearPvsData()', [
        ('_leaveTask?.WaitOne();', 'PVS: ожидание задачи выхода'),
        ('_deletionTask?.WaitOne();', 'PVS: ожидание задачи удаления'),
        ('_metadataMemory.Clear();', 'PVS: очистка памяти метаданных'),
    ])
    flow = root / 'Content.Server/GameTicking/GameTicker.RoundFlow.cs'
    trace_method(flow, 'private void ResettingCleanup()', [
        ('RaiseLocalEvent(ev);', 'Раунд: локальные обработчики очистки'),
        ('RaiseNetworkEvent(ev);', 'Раунд: сетевое событие очистки'),
        ('EntityManager.FlushEntities();', 'Раунд: удаление сущностей'),
        ('_mapManager.Restart();', 'Раунд: сброс карт'),
        ('_banManager.Restart();', 'Раунд: сброс ограничений'),
        ('_gameMapManager.ClearSelectedMap();', 'Раунд: сброс выбранной карты'),
        ('ClearGameRules();', 'Раунд: очистка игровых правил'),
    ])
    preset = root / 'Content.Server/GameTicking/GameTicker.GamePreset.cs'
    trace_method(preset, 'private void IncrementRoundNumber()', [
        ('var server = await _dbEntryManager.ServerEntity;', 'SQLite: получение записи сервера'),
        ('return await _db.AddNewRound(server, playerIds);', 'SQLite: создание записи раунда'),
        ('_taskManager.BlockWaitOnTask(task);', 'Раунд: ожидание записи в SQLite'),
        ('RoundId = task.GetAwaiter().GetResult();', 'Раунд: получение номера'),
    ])
    # A marker after return would be unreachable; log completion before returning.
    source = preset.read_text()
    source = source.replace('return await _db.AddNewRound(server, playerIds);\n            global::System.Console.WriteLine("[DONE] SQLite: создание записи раунда");',
        'var roundId = await _db.AddNewRound(server, playerIds);\n            global::System.Console.WriteLine("[DONE] SQLite: создание записи раунда");\n            return roundId;')
    preset.write_text(source)
    # Only the startup/restart method, not unrelated StartRound call sites.
    trace_method(flow, 'public void RestartRound()', [
        ('ResettingCleanup();', 'Раунд: полная очистка'),
        ('IncrementRoundNumber();', 'Раунд: регистрация в базе данных'),
        ('SendRoundStartingDiscordMessage();', 'Раунд: уведомление о запуске'),
        ('StartRound();', 'Раунд: запуск карты и режима'),
    ])
