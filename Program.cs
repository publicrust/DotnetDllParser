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

    // Выносим основную логику в отдельный метод
    static void ProcessDirectories(string sourceDir, string outputDir)
    {
        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
            Console.WriteLine($"Создана выходная директория: {outputDir}");
        }

        Console.WriteLine($"Начинаем парсинг DLL файлов из директории: {sourceDir}");
        Console.WriteLine($"Результаты будут сохранены в: {outputDir}");

        // Создаем резолвер сборок
        var assemblyResolver = CreateAssemblyResolver(sourceDir);

        foreach (string file in Directory.GetFiles(sourceDir, "*.dll"))
        {
            try
            {
                string fileName = Path.GetFileNameWithoutExtension(file);

                // Проверяем, является ли библиотека важной
                if (!ImportantLibraries.Any(lib => fileName.StartsWith(lib, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"Пропускаем неважную библиотеку: {fileName}");
                    continue;
                }

                string outputPath = Path.Combine(outputDir, fileName);

                if (!Directory.Exists(outputPath))
                {
                    Directory.CreateDirectory(outputPath);
                }

                Console.WriteLine($"Обработка важной библиотеки: {fileName}");

                // Создаем декомпилятор с резолвером сборок
                var decompiler = new CSharpDecompiler(file, assemblyResolver, new DecompilerSettings());
                var types = decompiler.TypeSystem.MainModule.TypeDefinitions;

                int skippedGeneratedTypes = 0;
                int processedTypes = 0;

                // Сохраняем каждый тип в отдельный файл
                foreach (var type in types)
                {
                    if (string.IsNullOrEmpty(type.Name)) continue;

                    // Пропускаем сгенерированные классы
                    if (IsGeneratedType(type))
                    {
                        skippedGeneratedTypes++;
                        continue;
                    }

                    // Создаем безопасное имя файла, заменяя недопустимые символы
                     string safeTypeName = string.Join("_", type.Name.Split(Path.GetInvalidFileNameChars()));
                     string typeFileName = $"{safeTypeName}.cstxt"; // Используем безопасное имя
                    string typeFilePath = Path.Combine(outputPath, typeFileName);

                    try
                    {
                        // Декомпилируем тип в C# код
                        string code = decompiler.DecompileAsString(type.MetadataToken);
                        File.WriteAllText(typeFilePath, code);
                        processedTypes++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка при декомпиляции типа {type.FullName} в файл {typeFileName}: {ex.Message}");
                    }
                }

                Console.WriteLine($"- Обработано типов: {processedTypes}, пропущено сгенерированных: {skippedGeneratedTypes}");
            }
            catch (BadImageFormatException)
            {
                Console.WriteLine($"Пропуск файла {Path.GetFileName(file)}, так как он не является валидной .NET сборкой.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Критическая ошибка при обработке файла {file}: {ex.Message}");
                // Можно добавить логирование стека вызовов: Console.WriteLine(ex.StackTrace);
            }
        }

        Console.WriteLine("Парсинг завершен!");
    }
}
