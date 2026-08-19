using DeskBox.Services;
using System.Reflection;

namespace DeskBox.Tests;

public sealed class LocalizationServiceLanguageTests
{
    public static IEnumerable<object[]> NewLanguages()
    {
        yield return [SettingsService.LanguageHindi, "hi"];
        yield return [SettingsService.LanguageSpanish, "es"];
        yield return [SettingsService.LanguageFrench, "fr"];
        yield return [SettingsService.LanguageArabic, "ar"];
        yield return [SettingsService.LanguageBengali, "bn"];
        yield return [SettingsService.LanguageRussian, "ru"];
    }

    public static IEnumerable<object[]> SupportedLocaleTables()
    {
        yield return ["ZhCn"];
        yield return ["JaJp"];
        yield return ["DeDe"];
        yield return ["PtBr"];
        yield return ["HiIn"];
        yield return ["EsEs"];
        yield return ["FrFr"];
        yield return ["ArSa"];
        yield return ["BnBd"];
        yield return ["RuRu"];
    }

    [Fact]
    public void AvailableLanguages_ContainsRequestedLocales()
    {
        var localization = TestServices.CreateLocalizationService();

        Assert.Contains(SettingsService.LanguageHindi, localization.AvailableLanguageSettings);
        Assert.Contains(SettingsService.LanguageSpanish, localization.AvailableLanguageSettings);
        Assert.Contains(SettingsService.LanguageFrench, localization.AvailableLanguageSettings);
        Assert.Contains(SettingsService.LanguageArabic, localization.AvailableLanguageSettings);
        Assert.Contains(SettingsService.LanguageBengali, localization.AvailableLanguageSettings);
        Assert.Contains(SettingsService.LanguageRussian, localization.AvailableLanguageSettings);
    }

    [Fact]
    public void VietnameseLocale_IsAvailableAndUsesAccentedCoreCopy()
    {
        var localization = TestServices.CreateLocalizationService(SettingsService.LanguageVietnamese);

        Assert.Contains(SettingsService.LanguageVietnamese, localization.AvailableLanguageSettings);
        Assert.Equal("vi", localization.ApiLanguageCode);
        Assert.Equal("Tiếng Việt", localization.GetLanguageDisplayName(SettingsService.LanguageVietnamese));
        Assert.Equal("Ngôn ngữ", localization.T("Settings.Language.Title"));
        Assert.Equal("Tự động sắp xếp tệp và thư mục mới", localization.T("DesktopOrganization.Auto.Title"));
        Assert.Equal("Thư mục", localization.T("DesktopOrganization.Category.Folders"));
        Assert.Equal("Có thể sắp xếp {0} mục vào {1} box", localization.T("DesktopOrganization.Preview.Headline"));
        Assert.Equal("Sắp xếp Desktop chưa hoàn tất", localization.T("DesktopOrganization.Result.FailedTitle"));
    }

    [Theory]
    [MemberData(nameof(NewLanguages))]
    public void NewLocale_ResolvesApiCodeAndCoreCopy(string language, string apiCode)
    {
        var localization = TestServices.CreateLocalizationService(language);

        Assert.Equal(language, localization.CurrentCultureName);
        Assert.Equal(apiCode, localization.ApiLanguageCode);
        Assert.NotEqual("Onboarding.Task.Step1.Title", localization.T("Onboarding.Task.Step1.Title"));
        Assert.NotEqual("Common.Paste", localization.T("Common.Paste"));
    }

    [Theory]
    [MemberData(nameof(NewLanguages))]
    public void NewLocale_ContainsEveryEnglishResourceKey(string language, string _)
    {
        var english = GetResourceTable("EnUs");
        var localized = GetResourceTable(language switch
        {
            SettingsService.LanguageHindi => "HiIn",
            SettingsService.LanguageSpanish => "EsEs",
            SettingsService.LanguageFrench => "FrFr",
            SettingsService.LanguageArabic => "ArSa",
            SettingsService.LanguageBengali => "BnBd",
            SettingsService.LanguageRussian => "RuRu",
            _ => throw new ArgumentOutOfRangeException(nameof(language))
        });

        Assert.Equal(
            english.Keys.OrderBy(key => key),
            localized.Keys.OrderBy(key => key));
    }

    [Theory]
    [MemberData(nameof(SupportedLocaleTables))]
    public void SupportedLocale_ContainsEveryEnglishResourceKey(string propertyName)
    {
        var english = GetResourceTable("EnUs");
        var localized = GetResourceTable(propertyName);

        Assert.Equal(
            english.Keys.OrderBy(key => key),
            localized.Keys.OrderBy(key => key));
    }

    [Theory]
    [MemberData(nameof(NewLanguages))]
    public void NormalizeLanguageSetting_PreservesNewLocale(string language, string _)
    {
        Assert.Equal(language, LocalizationService.NormalizeLanguageSetting(language));
    }

    private static IReadOnlyDictionary<string, string> GetResourceTable(string propertyName)
    {
        var property = typeof(LocalizationService).GetProperty(
            propertyName,
            BindingFlags.NonPublic | BindingFlags.Static);

        return Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(property?.GetValue(null));
    }
}
