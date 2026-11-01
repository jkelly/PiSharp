# Persistent task manager

The task board is persisted to tasks.json only by approval-gated tools.

Run: dotnet run --project samples/agents/05-PersistentTaskManager -- --workspace artifacts/persistent-task-manager

Test: dotnet run --project samples/agents/05-PersistentTaskManager -- --self-test

The demo reads the task board and checks task-501. With approval, mutating tools persist to tasks.json. Use --input path.json for another dataset. --live selects the configured model provider; domain data still comes from the JSON adapter.

To exercise a persistent update, run:

```powershell
dotnet run --project samples/agents/05-PersistentTaskManager -- --persist-demo --approve --workspace artifacts/task-board
```

This marks task-501 done in artifacts/task-board/tasks.json. Running again reads that saved board.
