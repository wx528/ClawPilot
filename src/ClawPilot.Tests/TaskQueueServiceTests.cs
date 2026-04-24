using ClawPilot.Core.Models;
using ClawPilot.Core.Services;
using Microsoft.Extensions.Logging;

namespace ClawPilot.Tests;

public class TaskQueueServiceTests
{
    [Fact]
    public async Task AddTaskAsync_ShouldAddTaskToQueue()
    {
        var tempDbPath = Path.GetTempFileName();
        try
        {
            var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<TaskQueueService>();
            var service = new TaskQueueService(tempDbPath, logger);
            
            await service.EnsureTableExistsAsync();
            
            var result = await service.AddTaskAsync("Test task message", "test-agent");
            
            Assert.True(result.Success);
            Assert.NotNull(result.TaskId);
            Assert.True(result.TaskId > 0);
        }
        finally
        {
            try
            {
                File.Delete(tempDbPath);
            }
            catch (IOException)
            {
                // 如果文件无法立即删除，等待一下再重试
                await Task.Delay(100);
                try
                {
                    File.Delete(tempDbPath);
                }
                catch
                {
                    // 如果仍然无法删除，忽略错误
                }
            }
        }
    }

    [Fact]
    public async Task GetTaskAsync_ShouldReturnExistingTask()
    {
        var tempDbPath = Path.GetTempFileName();
        try
        {
            var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<TaskQueueService>();
            
            int taskId;
            
            var service1 = new TaskQueueService(tempDbPath, logger);
            await service1.EnsureTableExistsAsync();
            var addResult = await service1.AddTaskAsync("Test task for get", "test-agent");
            
            Assert.True(addResult.Success);
            Assert.NotNull(addResult.TaskId);
            taskId = addResult.TaskId.Value;
            
            var service2 = new TaskQueueService(tempDbPath, logger);
            var getResult = await service2.GetTaskAsync(taskId);
            
            Assert.True(getResult.Success);
            Assert.NotNull(getResult.Data);
            var task = getResult.Data as TaskItem;
            Assert.NotNull(task);
            Assert.Equal(taskId, task.Id);
            Assert.Equal("Test task for get", task.Message);
        }
        finally
        {
            try
            {
                File.Delete(tempDbPath);
            }
            catch (IOException)
            {
                await Task.Delay(100);
                try
                {
                    File.Delete(tempDbPath);
                }
                catch
                {
                }
            }
        }
    }

    [Fact]
    public async Task ListTasksAsync_ShouldReturnAllTasks()
    {
        var tempDbPath = Path.GetTempFileName();
        try
        {
            var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<TaskQueueService>();
            
            var service1 = new TaskQueueService(tempDbPath, logger);
            await service1.EnsureTableExistsAsync();
            
            for (int i = 1; i <= 3; i++)
            {
                var result = await service1.AddTaskAsync($"Test task {i}", "test-agent");
                Assert.True(result.Success);
            }
            
            var service2 = new TaskQueueService(tempDbPath, logger);
            var tasks = await service2.ListTasksAsync();
            
            Assert.Equal(3, tasks.Count);
            Assert.True(tasks.All(t => t.Message.Contains("Test task")));
        }
        finally
        {
            try
            {
                File.Delete(tempDbPath);
            }
            catch (IOException)
            {
                await Task.Delay(100);
                try
                {
                    File.Delete(tempDbPath);
                }
                catch
                {
                }
            }
        }
    }

    [Fact]
    public async Task DeleteTaskAsync_ShouldDeleteTask()
    {
        var tempDbPath = Path.GetTempFileName();
        try
        {
            var logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<TaskQueueService>();
            
            int taskId;
            
            var service1 = new TaskQueueService(tempDbPath, logger);
            await service1.EnsureTableExistsAsync();
            var addResult = await service1.AddTaskAsync("Test task to delete", "test-agent");
            
            Assert.True(addResult.Success);
            Assert.NotNull(addResult.TaskId);
            taskId = addResult.TaskId.Value;
            
            var service2 = new TaskQueueService(tempDbPath, logger);
            var deleteResult = await service2.DeleteTaskAsync(taskId);
            
            Assert.True(deleteResult.Success);
            
            var service3 = new TaskQueueService(tempDbPath, logger);
            var getResult = await service3.GetTaskAsync(taskId);
            
            Assert.False(getResult.Success);
        }
        finally
        {
            try
            {
                File.Delete(tempDbPath);
            }
            catch (IOException)
            {
                await Task.Delay(100);
                try
                {
                    File.Delete(tempDbPath);
                }
                catch
                {
                }
            }
        }
    }
}