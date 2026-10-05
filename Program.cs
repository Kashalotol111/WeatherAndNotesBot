using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace WeatherAndNotesBot
{
    public class TaskItem
    {
        public string Text { get; set; } = string.Empty;
        public bool IsCompleted { get; set; } = false;
    }

    public class ScheduledTask
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public long ChatId { get; set; }
        public TimeSpan SendTime { get; set; }
        public bool IsWeather { get; set; }
        public string City { get; set; } = string.Empty;
        public DateTime? LastSentDate { get; set; }

        // Поля для заметок и повторов
        public List<TaskItem> Items { get; set; } = new();
        public bool IsStarted { get; set; } = false;
        public DateTime? NextReminderTime { get; set; }

        public bool AllCompleted => Items.Count > 0 && Items.All(i => i.IsCompleted);
    }

    public enum UserState
    {
        None,
        WaitingForWeatherInput,
        WaitingForNoteText,
        WaitingForNoteTime
    }

    public class UserSession
    {
        public UserState State { get; set; } = UserState.None;
        public List<string> PendingTasks { get; set; } = new();
    }

    class Program
    {
        // ⚠️ ЗАМЕНИТЕ ЭТИ КЛЮЧИ НА СВОИ:
        private const string BotToken = "8951639668:AAGYqwvMqpeurWCDGzgS5_UQN8qazZgNErQ";
        private const string WeatherApiKey = "dc17fb2abe51957d03346f2f6f154567";

        private static TelegramBotClient _botClient = null!;
        private static readonly List<ScheduledTask> _tasks = new();
        private static readonly Dictionary<long, UserSession> _userSessions = new();

        // 3 главные кнопки снизу
        private static readonly ReplyKeyboardMarkup MainMenu = new(new[]
        {
            new KeyboardButton[] { "☀️ Погода", "📌 Добавить заметку" },
            new KeyboardButton[] { "✅ Выполнение заметок" }
        })
        {
            ResizeKeyboard = true
        };

        static async Task Main(string[] args)
        {
            _botClient = new TelegramBotClient(BotToken);
            using var cts = new CancellationTokenSource();

            _botClient.StartReceiving(
                HandleUpdateAsync,
                HandleErrorAsync,
                new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() },
                cancellationToken: cts.Token
            );

            Console.WriteLine("Бот запущен. Нажмите Enter для выхода.");
            _ = Task.Run(() => SchedulerLoopAsync(cts.Token));

            Console.ReadLine();
            cts.Cancel();
        }

        private static UserSession GetSession(long chatId)
        {
            lock (_userSessions)
            {
                if (!_userSessions.TryGetValue(chatId, out var session))
                {
                    session = new UserSession();
                    _userSessions[chatId] = session;
                }
                return session;
            }
        }

        private static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken ct)
        {
            // Обработка нажатий на кнопки задач и времени
            if (update.CallbackQuery is { } callbackQuery)
            {
                await HandleCallbackQueryAsync(botClient, callbackQuery, ct);
                return;
            }

            if (update.Message is not { Text: { } messageText } message) return;
            var chatId = message.Chat.Id;
            var session = GetSession(chatId);

            // 1. Команда /start
            if (messageText.StartsWith("/start"))
            {
                session.State = UserState.None;
                await botClient.SendMessage(
                    chatId,
                    "Привет я создан для облегчения твоей жизни. Выбери время в которое ты хочешь получать сводку погоды или отслеживай задачи:",
                    replyMarkup: MainMenu,
                    cancellationToken: ct
                );
            }
            // 2. Кнопка "☀️ Погода"
            else if (messageText == "☀️ Погода")
            {
                session.State = UserState.WaitingForWeatherInput;
                await botClient.SendMessage(
                    chatId,
                    "🌤 Укажите город и время отправки погоды через пробел (например: `Москва 08:00`):",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: ct
                );
            }
            // 3. Кнопка "📌 Добавить заметку"
            else if (messageText == "📌 Добавить заметку")
            {
                session.State = UserState.WaitingForNoteText;
                await botClient.SendMessage(
                    chatId,
                    "📌 Напишите список ваших задач (по одной задаче на каждой строке):\n\n*Пример:*\nКупить молоко\nЗайти в банк\nПозвонить маме",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: ct
                );
            }
            // 4. Кнопка "✅ Выполнение заметок"
            else if (messageText == "✅ Выполнение заметок")
            {
                session.State = UserState.None;
                await ShowUserNotesAsync(botClient, chatId, ct);
            }
            // Текстовый ввод пользователя
            else
            {
                if (session.State == UserState.WaitingForWeatherInput)
                {
                    if (TryParseCityAndTime(messageText, out var city, out var time))
                    {
                        lock (_tasks)
                        {
                            _tasks.RemoveAll(t => t.ChatId == chatId && t.IsWeather);
                            _tasks.Add(new ScheduledTask
                            {
                                ChatId = chatId,
                                SendTime = time,
                                IsWeather = true,
                                City = city
                            });
                        }
                        session.State = UserState.None;
                        await botClient.SendMessage(
                            chatId,
                            $"✅ Погода зафиксирована на {time:hh\\:mm} для города {city}!",
                            replyMarkup: MainMenu,
                            cancellationToken: ct
                        );
                    }
                    else
                    {
                        await botClient.SendMessage(
                            chatId,
                            "❌ Неверный формат. Напишите город и время (например: `Москва 08:00`)",
                            parseMode: ParseMode.Markdown,
                            cancellationToken: ct
                        );
                    }
                }
                else if (session.State == UserState.WaitingForNoteText)
                {
                    var lines = messageText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                           .Select(s => s.Trim())
                                           .Where(s => !string.IsNullOrWhiteSpace(s))
                                           .ToList();

                    if (lines.Count == 0)
                    {
                        await botClient.SendMessage(chatId, "❌ Введите хотя бы одну задачу.", cancellationToken: ct);
                        return;
                    }

                    session.PendingTasks = lines;
                    session.State = UserState.WaitingForNoteTime;

                    var timeKeyboard = new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("⏰ 08:00", "time_08:00"),
                            InlineKeyboardButton.WithCallbackData("⏰ 12:00", "time_12:00"),
                            InlineKeyboardButton.WithCallbackData("⏰ 18:00", "time_18:00")
                        },
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("✍️ Указать время вручную", "enter_time_manual")
                        }
                    });

                    await botClient.SendMessage(
                        chatId,
                        $"📝 Добавлено задач: {lines.Count}.\n\n👇 Выберите время первой отправки заметки:",
                        replyMarkup: timeKeyboard,
                        cancellationToken: ct
                    );
                }
                else if (session.State == UserState.WaitingForNoteTime)
                {
                    if (TimeSpan.TryParse(messageText, out var time))
                    {
                        SaveNoteTask(chatId, session.PendingTasks, time);
                        await botClient.SendMessage(
                            chatId,
                            $"✅ Заметка создана на {time:hh\\:mm}! Вы можете отмечать выполненные задачи в разделе «✅ Выполнение заметок».",
                            replyMarkup: MainMenu,
                            cancellationToken: ct
                        );

                        session.State = UserState.None;
                        session.PendingTasks.Clear();
                    }
                    else
                    {
                        await botClient.SendMessage(
                            chatId,
                            "❌ Введите время в формате `ЧЧ:ММ` (например: `14:30`)",
                            parseMode: ParseMode.Markdown,
                            cancellationToken: ct
                        );
                    }
                }
            }
        }

        private static async Task HandleCallbackQueryAsync(ITelegramBotClient botClient, CallbackQuery callbackQuery, CancellationToken ct)
        {
            var chatId = callbackQuery.Message!.Chat.Id;
            var data = callbackQuery.Data;
            var session = GetSession(chatId);

            await botClient.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: ct);

            if (data == null) return;

            // Выбор времени из предложенных кнопок
            if (data.StartsWith("time_"))
            {
                var timeStr = data.Replace("time_", "");
                if (TimeSpan.TryParse(timeStr, out var time))
                {
                    SaveNoteTask(chatId, session.PendingTasks, time);
                    await botClient.SendMessage(
                        chatId,
                        $"✅ Заметка сохранена на {time:hh\\:mm}! Вы можете отмечать задачи заранее по кнопке «✅ Выполнение заметок».",
                        replyMarkup: MainMenu,
                        cancellationToken: ct
                    );

                    session.State = UserState.None;
                    session.PendingTasks.Clear();
                }
            }
            else if (data == "enter_time_manual")
            {
                await botClient.SendMessage(
                    chatId,
                    "⏰ Введите время в формате `ЧЧ:ММ` (например: `14:30`):",
                    parseMode: ParseMode.Markdown,
                    cancellationToken: ct
                );
            }
            // Нажатие на кнопку выполнения задачи: format "tog_{noteId}_{itemIndex}"
            else if (data.StartsWith("tog_"))
            {
                var parts = data.Split('_');
                if (parts.Length == 3 && int.TryParse(parts[2], out var index))
                {
                    var noteId = parts[1];
                    ScheduledTask? note;

                    lock (_tasks)
                    {
                        note = _tasks.FirstOrDefault(t => t.Id == noteId);
                        if (note != null && index >= 0 && index < note.Items.Count)
                        {
                            note.Items[index].IsCompleted = !note.Items[index].IsCompleted;
                        }
                    }

                    if (note != null)
                    {
                        // Обновляем сообщение и кнопки на лету
                        var keyboard = BuildNoteKeyboard(note);
                        var statusHeader = note.AllCompleted
                            ? "🎉 **ВСЕ ЗАДАЧИ ВЫПОЛНЕНЫ!**"
                            : $"📋 **Заметка на {note.SendTime:hh\\:mm}:**";

                        try
                        {
                            await botClient.EditMessageText(
                                chatId,
                                callbackQuery.Message.MessageId,
                                statusHeader,
                                parseMode: ParseMode.Markdown,
                                replyMarkup: keyboard,
                                cancellationToken: ct
                            );
                        }
                        catch { /* Игнорируем ошибку, если текст сообщения не изменился */ }
                    }
                }
            }
        }

        private static async Task ShowUserNotesAsync(ITelegramBotClient botClient, long chatId, CancellationToken ct)
        {
            List<ScheduledTask> userNotes;
            lock (_tasks)
            {
                userNotes = _tasks.Where(t => t.ChatId == chatId && !t.IsWeather).ToList();
            }

            if (!userNotes.Any())
            {
                await botClient.SendMessage(chatId, "У вас пока нет активных заметок.", cancellationToken: ct);
                return;
            }

            foreach (var note in userNotes)
            {
                var keyboard = BuildNoteKeyboard(note);
                var header = note.AllCompleted
                    ? "🎉 **ВСЕ ЗАДАЧИ ВЫПОЛНЕНЫ!**"
                    : $"📋 **Заметка на {note.SendTime:hh\\:mm}:**";

                await botClient.SendMessage(
                    chatId,
                    header,
                    parseMode: ParseMode.Markdown,
                    replyMarkup: keyboard,
                    cancellationToken: ct
                );
            }
        }

        private static InlineKeyboardMarkup BuildNoteKeyboard(ScheduledTask note)
        {
            var rows = new List<InlineKeyboardButton[]>();

            for (int i = 0; i < note.Items.Count; i++)
            {
                var item = note.Items[i];
                var icon = item.IsCompleted ? "✅" : "❌";
                var buttonText = $"{icon} {item.Text}";

                rows.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData(buttonText, $"tog_{note.Id}_{i}")
                });
            }

            return new InlineKeyboardMarkup(rows);
        }

        private static void SaveNoteTask(long chatId, List<string> taskTexts, TimeSpan time)
        {
            lock (_tasks)
            {
                _tasks.Add(new ScheduledTask
                {
                    ChatId = chatId,
                    SendTime = time,
                    IsWeather = false,
                    Items = taskTexts.Select(t => new TaskItem { Text = t, IsCompleted = false }).ToList()
                });
            }
        }

        private static bool TryParseCityAndTime(string input, out string city, out TimeSpan time)
        {
            city = string.Empty;
            time = TimeSpan.Zero;
            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 2) return false;

            if (TimeSpan.TryParse(parts[0], out time))
            {
                city = parts[1];
                return true;
            }
            if (TimeSpan.TryParse(parts[1], out time))
            {
                city = parts[0];
                return true;
            }

            return false;
        }

        private static Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken ct)
        {
            Console.WriteLine($"Ошибка Telegram API: {exception.Message}");
            return Task.CompletedTask;
        }

        // Фоновый поток: отправка погоды и регулярные напоминания каждые 30 минут
        private static async Task SchedulerLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.Now;
                var currentTime = now.TimeOfDay;
                List<ScheduledTask> dueTasks = new();

                lock (_tasks)
                {
                    // Удаляем полностью выполненные заметки из памяти
                    _tasks.RemoveAll(t => !t.IsWeather && t.AllCompleted);

                    foreach (var task in _tasks.ToList())
                    {
                        if (task.IsWeather)
                        {
                            // Проверка погоды (1 раз в день)
                            if (Math.Abs((task.SendTime - currentTime).TotalMinutes) < 1 && task.LastSentDate?.Date != now.Date)
                            {
                                task.LastSentDate = now.Date;
                                dueTasks.Add(task);
                            }
                        }
                        else
                        {
                            // Заметки
                            if (!task.IsStarted)
                            {
                                // Первичный запуск в установленное время
                                if (Math.Abs((task.SendTime - currentTime).TotalMinutes) < 1)
                                {
                                    task.IsStarted = true;
                                    task.NextReminderTime = now.AddMinutes(30);
                                    dueTasks.Add(task);
                                }
                            }
                            else
                            {
                                // Повторное напоминание каждые 30 минут, пока не стоят ВСЕ галочки
                                if (task.NextReminderTime.HasValue && now >= task.NextReminderTime.Value)
                                {
                                    task.NextReminderTime = now.AddMinutes(30);
                                    dueTasks.Add(task);
                                }
                            }
                        }
                    }
                }

                foreach (var task in dueTasks)
                {
                    try
                    {
                        if (task.IsWeather)
                        {
                            string weather = await GetWeatherAsync(task.City);
                            await _botClient.SendMessage(
                                task.ChatId,
                                $"🌤 **Прогноз погоды ({task.City}):**\n\n{weather}",
                                parseMode: ParseMode.Markdown,
                                cancellationToken: ct
                            );
                        }
                        else
                        {
                            var keyboard = BuildNoteKeyboard(task);
                            await _botClient.SendMessage(
                                task.ChatId,
                                "🔔 **Напоминание о невыполненных задачах!**\nОтметьте выполненные галочкой:",
                                replyMarkup: keyboard,
                                cancellationToken: ct
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка отправки: {ex.Message}");
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }

        private static async Task<string> GetWeatherAsync(string city)
        {
            try
            {
                using var http = new HttpClient();
                var url = $"https://api.openweathermap.org/data/2.5/weather?q={city}&appid={WeatherApiKey}&units=metric&lang=ru";
                var response = await http.GetStringAsync(url);
                using var doc = JsonDocument.Parse(response);

                var temp = doc.RootElement.GetProperty("main").GetProperty("temp").GetDouble();
                var feelsLike = doc.RootElement.GetProperty("main").GetProperty("feels_like").GetDouble();
                var desc = doc.RootElement.GetProperty("weather")[0].GetProperty("description").GetString();

                return $"• Температура: {temp:F1}°C (ощущается как {feelsLike:F1}°C)\n• На улице: {desc}";
            }
            catch
            {
                return "Не удалось загрузить данные о погоде. Проверьте правильность названия города.";
            }
        }
    }
}