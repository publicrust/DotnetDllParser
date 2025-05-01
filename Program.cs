using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using System.Text;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.IL;
using System.Text.RegularExpressions;
using System.Linq; // Added for Any() and LINQ methods
using System.Collections.Generic; // Added for HashSet

partial class Program
{
    private static readonly HashSet<string> ImportantLibraries = new HashSet<string>
    {
        "Facepunch",
        "Assembly-CSharp",
        "Oxide",
        "Rust",
        "0Harmony"
    };

    // Добавляем список префиксов для сгенерированных классов, которые следует пропускать
    private static readonly HashSet<string> GeneratedClassPrefixes = new HashSet<string>
    {
        "__StaticArrayInit",
        "<>",
        "<PrivateImplementationDetails>",
        "EmbeddedAttribute",
        "IsReadOnlyAttribute",
        "<Module>",
        "$ArrayType="
    };

    // Регулярное выражение для проверки наличия GUID в имени класса
    private static readonly Regex GuidPattern = new Regex(@"<[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}>", RegexOptions.Compiled);
    
    // Регулярное выражение для проверки сгенерированных классов итераторов и асинхронных методов
    private static readonly Regex GeneratedMethodPattern = new Regex(@"<.*>d__\d+", RegexOptions.Compiled);
    
    // Регулярное выражение для проверки классов с фиксированными буферами
    private static readonly Regex FixedBufferPattern = new Regex(@"<.*>e__FixedBuffer", RegexOptions.Compiled);
    
    // Регулярное выражение для проверки анонимных классов
    private static readonly Regex AnonStoreyPattern = new Regex(@"<.*>c__AnonStorey\d+", RegexOptions.Compiled);
    
    // Регулярное выражение для проверки итераторов (старый стиль)
    private static readonly Regex IteratorPattern = new Regex(@"<.*>c__Iterator\d+", RegexOptions.Compiled);

    // Функция для проверки, является ли класс сгенерированным
    private static bool IsGeneratedType(ITypeDefinition type)
    {
        // Проверяем префиксы
        if (GeneratedClassPrefixes.Any(prefix => type.Name.StartsWith(prefix)))
            return true;
        
        // Проверяем наличие GUID в имени
        if (GuidPattern.IsMatch(type.Name))
            return true;
        
        // Проверяем итераторы и асинхронные методы
        if (GeneratedMethodPattern.IsMatch(type.Name))
            return true;
        
        // Проверяем классы с фиксированными буферами
        if (FixedBufferPattern.IsMatch(type.Name))
            return true;
        
        // Проверяем анонимные классы
        if (AnonStoreyPattern.IsMatch(type.Name))
            return true;
        
        // Проверяем итераторы (старый стиль)
        if (IteratorPattern.IsMatch(type.Name))
            return true;
        
        // Проверяем атрибуты компилятора
        if (type.Name.Contains("Attribute") && (
            type.Name.Contains("CompilerGenerated") || 
            type.Name.Contains("NullableContext") || 
            type.Name.Contains("Nullable")))
            return true;
        
        // Проверяем другие специфические шаблоны
        if (type.Name.StartsWith("<") && type.Name.Contains("g__"))
            return true;
        
        return false;
    }

    private static UniversalAssemblyResolver CreateAssemblyResolver(string baseDir)
    {
        var resolver = new UniversalAssemblyResolver(baseDir, false, null);
        return resolver;
    }

    static int Main(string[] args)
    {
        var sourceOption = new Option<DirectoryInfo>(
            aliases: new[] { "-s", "--source" },
            description: "Директория с исходными DLL файлами.")
        {
            IsRequired = true // Делаем аргумент обязательным
        };
        sourceOption.AddValidator(result => // Валидатор для проверки существования директории
        {
            if (!result.GetValueOrDefault<DirectoryInfo>()?.Exists ?? true)
            {
                result.ErrorMessage = $"Директория {result.Tokens.Single().Value} не найдена.";
            }
        });


        var outputOption = new Option<DirectoryInfo>(
             aliases: new[] { "-o", "--output" },
             description: "Директория для сохранения декомпилированных файлов.")
         {
             IsRequired = true // Делаем аргумент обязательным
         };

        var rootCommand = new RootCommand("Декомпилятор DLL файлов в текстовые C# файлы.")
        {
            sourceOption,
            outputOption
        };

        rootCommand.SetHandler((sourceDir, outputDir) =>
        {
            ProcessDirectories(sourceDir.FullName, outputDir.FullName);
        }, sourceOption, outputOption);

        return rootCommand.Invoke(args);
    }

    const long MaxChunkSizeBytes = 950 * 1024; // Примерно 950 KB

    // Выносим основную логику в отдельный метод
    static void ProcessDirectories(string sourceDir, string outputDir)
    {
        // Создаем базовую выходную директорию, если ее нет
        Directory.CreateDirectory(outputDir); 
        Console.WriteLine($"Базовая выходная директория: {outputDir}");


        Console.WriteLine($"Начинаем парсинг DLL файлов из директории: {sourceDir}");
        // Console.WriteLine($"Результаты будут сохранены в поддиректориях {outputDir}"); // Изменено сообщение

        // Создаем резолвер сборок
        var assemblyResolver = CreateAssemblyResolver(sourceDir);

        foreach (string file in Directory.GetFiles(sourceDir, "*.dll"))
        {
            string baseFileName = Path.GetFileNameWithoutExtension(file);
            try
            {
                 // Проверяем, является ли библиотека важной
                if (!ImportantLibraries.Any(lib => baseFileName.StartsWith(lib, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"Пропускаем неважную библиотеку: {baseFileName}");
                    continue;
                }

                Console.WriteLine($"Обработка важной библиотеки: {baseFileName}");

                // Создаем поддиректорию для текущей DLL
                string dllOutputDir = Path.Combine(outputDir, baseFileName);
                Directory.CreateDirectory(dllOutputDir);
                Console.WriteLine($"Обработка важной библиотеки: {baseFileName} -> {dllOutputDir}");

                // Создаем декомпилятор с резолвером сборок
                var decompiler = new CSharpDecompiler(file, assemblyResolver, new DecompilerSettings());
                var types = decompiler.TypeSystem.MainModule.TypeDefinitions;

                int skippedGeneratedTypes = 0;
                int processedTypes = 0;
                int chunkCount = 1;
                StringBuilder currentChunkContent = new StringBuilder();

                 // Формируем путь к файлу чанка ВНУТРИ поддиректории DLL
                 string GetChunkFilePath(int count) => Path.Combine(dllOutputDir, $"{baseFileName}_chunk{count}.cstxt");
                 string currentChunkFilePath = GetChunkFilePath(chunkCount);

                // Обрабатываем типы
                foreach (var type in types)
                {
                    if (string.IsNullOrEmpty(type.Name)) continue;

                    // Пропускаем сгенерированные классы
                    if (IsGeneratedType(type))
                    {
                        skippedGeneratedTypes++;
                        continue;
                    }

                    try
                    {
                        // Декомпилируем тип в C# код
                        string code = decompiler.DecompileAsString(type.MetadataToken);
                        string codeWithSeparator = code + Environment.NewLine + Environment.NewLine; // Добавляем разделитель

                        // Проверяем, нужно ли начинать новый чанк
                        // Проверяем только если в текущем чанке уже есть содержимое
                        if (currentChunkContent.Length > 0 && currentChunkContent.Length + codeWithSeparator.Length > MaxChunkSizeBytes)
                        {
                             // Записываем текущий чанк
                            File.WriteAllText(currentChunkFilePath, currentChunkContent.ToString());
                            Console.WriteLine($"  - Записан чанк: {Path.GetFileName(currentChunkFilePath)} ({currentChunkContent.Length} байт)");
                            
                            // Начинаем новый чанк
                            chunkCount++;
                            currentChunkFilePath = GetChunkFilePath(chunkCount);
                            currentChunkContent.Clear();
                        }

                        // Добавляем код в текущий чанк
                        currentChunkContent.Append(codeWithSeparator);
                        processedTypes++;
                    }
                    catch (Exception ex)
                    {
                        // Используем безопасное имя типа для логгирования
                        string safeTypeName = string.Join("_", type.Name.Split(Path.GetInvalidFileNameChars()));
                        Console.WriteLine($"Ошибка при декомпиляции типа {type.FullName} (в {safeTypeName}): {ex.Message}");
                    }
                }
                 // Записываем последний чанк, если он не пустой
                if (currentChunkContent.Length > 0)
                {
                    File.WriteAllText(currentChunkFilePath, currentChunkContent.ToString());
                    Console.WriteLine($"  - Записан чанк: {Path.GetFileName(currentChunkFilePath)} ({currentChunkContent.Length} байт)");
                }


                Console.WriteLine($"-> {baseFileName}: Обработано типов: {processedTypes}, пропущено сгенерированных: {skippedGeneratedTypes}, создано чанков: {chunkCount}");
            }
            catch (BadImageFormatException)
            {
                Console.WriteLine($"Пропуск файла {baseFileName}, так как он не является валидной .NET сборкой.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Критическая ошибка при обработке файла {baseFileName}: {ex.Message}");
                // Console.WriteLine(ex.StackTrace);
            }
        }

        Console.WriteLine("Парсинг завершен!");
    }
}
