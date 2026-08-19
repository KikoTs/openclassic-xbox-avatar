#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <wincrypt.h>
#include <inspectable.h>
#include <objbase.h>

#include <algorithm>
#include <bit>
#include <chrono>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <set>
#include <string>
#include <vector>

#include <winrt/base.h>
#include <winrt/Windows.ApplicationModel.Core.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Storage.h>
#include <winrt/Windows.UI.Core.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Controls.h>
#include <winrt/Windows.UI.Xaml.Data.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <winrt/Avatars.Foundation.API.h>
#include <winrt/Avatars.FoundationPrivate.API.h>
#include <winrt/Avatars.FoundationPrivate.Adapter.h>

namespace fs = std::filesystem;
namespace avatar = winrt::Avatars::FoundationPrivate;
namespace public_avatar = winrt::Avatars::Foundation;

namespace
{
    volatile LONG g_bridgeRunning = 0;

    bool IsAvatarManifest(const std::wstring& candidate);

    struct BridgeRunReset
    {
        ~BridgeRunReset()
        {
            InterlockedExchange(&g_bridgeRunning, 0);
        }
    };

    fs::path LocalStatePath()
    {
        wchar_t localAppData[32768]{};
        const DWORD length = GetEnvironmentVariableW(
            L"LOCALAPPDATA", localAppData, static_cast<DWORD>(std::size(localAppData)));
        if (!length || length >= std::size(localAppData))
        {
            return L".";
        }

        // LOCALAPPDATA is redirected to this package's private AC folder when
        // the bridge runs inside Xbox Original Avatars.
        return fs::path(localAppData);
    }

    void Log(std::wofstream& stream, const std::wstring& message)
    {
        stream << message << L'\n';
        stream.flush();
    }

    void LogInspectableIids(
        std::wofstream& log,
        const std::wstring& label,
        const winrt::Windows::Foundation::IInspectable& object)
    {
        ULONG count = 0;
        IID* identifiers = nullptr;
        auto* inspectable = reinterpret_cast<::IInspectable*>(winrt::get_abi(object));
        if (!inspectable || FAILED(inspectable->GetIids(&count, &identifiers)))
        {
            return;
        }
        std::wstring description = L"IIDs for " + label + L":";
        for (ULONG index = 0; index < count; ++index)
        {
            wchar_t text[64]{};
            StringFromGUID2(identifiers[index], text, static_cast<int>(std::size(text)));
            description += L" " + std::wstring(text);
        }
        CoTaskMemFree(identifiers);
        Log(log, description);
    }

    std::vector<unsigned char> DecodeManifest(const winrt::hstring& manifest)
    {
        DWORD byteCount = 0;
        if (!CryptStringToBinaryW(
                manifest.c_str(),
                static_cast<DWORD>(manifest.size()),
                CRYPT_STRING_BASE64,
                nullptr,
                &byteCount,
                nullptr,
                nullptr))
        {
            return {};
        }

        std::vector<unsigned char> bytes(byteCount);
        if (!CryptStringToBinaryW(
                manifest.c_str(),
                static_cast<DWORD>(manifest.size()),
                CRYPT_STRING_BASE64,
                bytes.data(),
                &byteCount,
                nullptr,
                nullptr))
        {
            return {};
        }

        bytes.resize(byteCount);
        return bytes;
    }

    std::wstring FormatManifestAssetId(
        const std::vector<unsigned char>& bytes,
        size_t offset)
    {
        if (offset + 16 > bytes.size())
        {
            return {};
        }

        wchar_t assetId[37]{};
        swprintf_s(
            assetId,
            L"%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
            bytes[offset + 0], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3],
            bytes[offset + 4], bytes[offset + 5], bytes[offset + 6], bytes[offset + 7],
            bytes[offset + 8], bytes[offset + 9], bytes[offset + 10], bytes[offset + 11],
            bytes[offset + 12], bytes[offset + 13], bytes[offset + 14], bytes[offset + 15]);
        return assetId;
    }

    std::wstring ManifestAssetId(const winrt::hstring& manifest, size_t offset)
    {
        return FormatManifestAssetId(DecodeManifest(manifest), offset);
    }

    std::map<unsigned int, std::wstring> ManifestAssets(const winrt::hstring& manifest)
    {
        const auto bytes = DecodeManifest(manifest);
        std::map<unsigned int, std::wstring> assets;
        const unsigned char assetTail[] = {0xC1, 0xC8, 0xF1, 0x09, 0xA1, 0x9C, 0xB2, 0xE0};
        for (size_t offset = 0; offset + 16 <= bytes.size(); ++offset)
        {
            if (memcmp(bytes.data() + offset + 8, assetTail, sizeof(assetTail)) != 0)
            {
                continue;
            }
            const unsigned int mask =
                (static_cast<unsigned int>(bytes[offset]) << 24) |
                (static_cast<unsigned int>(bytes[offset + 1]) << 16) |
                (static_cast<unsigned int>(bytes[offset + 2]) << 8) |
                static_cast<unsigned int>(bytes[offset + 3]);
            if (mask)
            {
                assets.try_emplace(mask, FormatManifestAssetId(bytes, offset));
            }
        }
        return assets;
    }

    winrt::hstring FindAvatarManifestInObject(
        std::wofstream& log,
        const winrt::Windows::Foundation::IInspectable& object,
        int depth)
    {
        if (!object || depth > 4)
        {
            return {};
        }

        if (const auto proxyObject = object.try_as<avatar::API::IFoundationProxy>())
        {
            const auto proxyScene = proxyObject.Scene();
            if (proxyScene)
            {
                const auto current = proxyScene.CurrentAvatar();
                if (current && !current.Manifest().empty())
                {
                    Log(log, L"Found the editor avatar through its private foundation proxy.");
                    return current.Manifest();
                }
            }
        }
        if (const auto proxyObject = object.try_as<public_avatar::API::IFoundationProxy>())
        {
            const auto proxyScene = proxyObject.Scene();
            if (proxyScene)
            {
                const auto current = proxyScene.CurrentAvatar();
                if (current && !current.Manifest().empty())
                {
                    Log(log, L"Found the editor avatar through its public foundation proxy.");
                    return current.Manifest();
                }
            }
        }
        if (const auto sceneObject = object.try_as<avatar::API::IAvatarScene>())
        {
            const auto current = sceneObject.CurrentAvatar();
            if (current && !current.Manifest().empty())
            {
                Log(log, L"Found the editor avatar through its private scene.");
                return current.Manifest();
            }
        }
        if (const auto sceneObject = object.try_as<public_avatar::API::IAvatarScene>())
        {
            const auto current = sceneObject.CurrentAvatar();
            if (current && !current.Manifest().empty())
            {
                Log(log, L"Found the editor avatar through its public scene.");
                return current.Manifest();
            }
        }

        if (const auto avatarObject = object.try_as<avatar::API::IAvatar>())
        {
            const auto value = avatarObject.Manifest();
            if (!value.empty())
            {
                Log(log, L"Found the editor's current IAvatar manifest.");
                return value;
            }
        }
        if (const auto avatarObject = object.try_as<public_avatar::API::IAvatar>())
        {
            const auto value = avatarObject.Manifest();
            if (!value.empty())
            {
                Log(log, L"Found the editor's public IAvatar manifest.");
                return value;
            }
        }

        const auto provider =
            object.try_as<winrt::Windows::UI::Xaml::Data::ICustomPropertyProvider>();
        if (!provider)
        {
            return {};
        }

        try
        {
            const auto type = provider.Type();
            Log(log, L"Managed object type: " + std::wstring(type.Name.c_str()) +
                         L" kind=" + std::to_wstring(static_cast<int>(type.Kind)));
        }
        catch (const winrt::hresult_error&)
        {
        }

        const wchar_t* propertyNames[] = {
            L"CurrentAvatar",
            L"AvatarService",
            L"AvatarFoundationService",
            L"AvatarManifestService",
            L"ManifestService",
            L"AvatarCore",
            L"Scene",
            L"Avatar",
            L"Manifest",
            L"DirtyManifest",
            L"LastKnownSavedManifest",
            L"AvatarManifest",
            L"CurrentManifest",
            L"FoundationService",
            L"AvatarFoundation",
            L"AvatarFoundationServiceProxy",
            L"Container",
            L"SessionState",
            L"SessionStateService",
            L"UserService",
            L"AuthService",
            L"CacheService",
            L"Values",
        };
        for (const auto* propertyName : propertyNames)
        {
            try
            {
                const auto property = provider.GetCustomProperty(propertyName);
                if (!property)
                {
                    continue;
                }
                const auto value = property.GetValue(object);
                if (!value)
                {
                    continue;
                }
                Log(log, std::wstring(L"Application property found: ") + propertyName +
                             L" class=" + std::wstring(winrt::get_class_name(value).c_str()));
                if (wcscmp(propertyName, L"CurrentAvatar") == 0 ||
                    wcscmp(propertyName, L"AvatarCore") == 0 ||
                    wcscmp(propertyName, L"Scene") == 0)
                {
                    LogInspectableIids(log, propertyName, value);
                }
                if (const auto propertyValue =
                        value.try_as<winrt::Windows::Foundation::IPropertyValue>())
                {
                    if (propertyValue.Type() ==
                        winrt::Windows::Foundation::PropertyType::String)
                    {
                        const auto text = propertyValue.GetString();
                        Log(log, std::wstring(L"String property length: ") + propertyName +
                                     L"=" + std::to_wstring(text.size()));
                        if (text.size() >= 1000)
                        {
                            Log(log, std::wstring(L"Found avatar manifest property: ") +
                                         propertyName);
                            return text;
                        }
                    }
                }
                const auto manifest = FindAvatarManifestInObject(log, value, depth + 1);
                if (!manifest.empty())
                {
                    return manifest;
                }
            }
            catch (const winrt::hresult_error&)
            {
            }
        }
        return {};
    }

    winrt::hstring FindManifestWithXamlBinding(
        std::wofstream& log,
        const winrt::Windows::Foundation::IInspectable& source)
    {
        if (!source)
        {
            return {};
        }
        const wchar_t* paths[] = {
            L"Manifest",
            L"Avatar.Manifest",
            L"CurrentAvatar.Manifest",
            L"FoundationService.CurrentAvatar.Manifest",
            L"FoundationService.AvatarCore.Manifest",
            L"Avatar.FoundationService.CurrentAvatar.Manifest",
            L"Avatar.CurrentAvatar.Manifest",
        };
        for (const auto* path : paths)
        {
            try
            {
                winrt::Windows::UI::Xaml::Controls::TextBlock probe;
                winrt::Windows::UI::Xaml::Data::Binding binding;
                binding.Source(source);
                binding.Path(winrt::Windows::UI::Xaml::PropertyPath(path));
                binding.Mode(winrt::Windows::UI::Xaml::Data::BindingMode::OneTime);
                probe.SetBinding(
                    winrt::Windows::UI::Xaml::Controls::TextBlock::TextProperty(),
                    binding);
                const auto text = probe.Text();
                if (text.size() == 1336 && IsAvatarManifest(std::wstring(text.c_str())))
                {
                    Log(log, std::wstring(L"Found avatar manifest through XAML binding: ") +
                                 path);
                    return text;
                }
            }
            catch (const winrt::hresult_error&)
            {
            }
        }
        return {};
    }

    winrt::hstring FindEditorAvatarManifest(std::wofstream& log)
    {
        const auto application = winrt::Windows::UI::Xaml::Application::Current();
        if (const auto manifest = FindAvatarManifestInObject(log, application, 0);
            !manifest.empty())
        {
            Log(log, L"Saved avatar discovered on the XAML Application object.");
            return manifest;
        }

        const auto applicationResources =
            application.Resources();
        Log(log, L"Application resource count: " +
                     std::to_wstring(applicationResources.Size()));
        for (const auto& pair : applicationResources)
        {
            const auto value = pair.Value();
            if (value)
            {
                Log(log, L"Application resource class: " +
                             std::wstring(winrt::get_class_name(value).c_str()));
                const auto manifest = FindAvatarManifestInObject(log, value, 0);
                if (!manifest.empty())
                {
                    return manifest;
                }
            }
        }

        const auto coreProperties =
            winrt::Windows::ApplicationModel::Core::CoreApplication::Properties();
        Log(log, L"CoreApplication property count: " +
                     std::to_wstring(coreProperties.Size()));
        for (const auto& pair : coreProperties)
        {
            Log(log, L"CoreApplication property: " + std::wstring(pair.Key().c_str()));
            const auto manifest = FindAvatarManifestInObject(log, pair.Value(), 0);
            if (!manifest.empty())
            {
                return manifest;
            }
        }

        const auto root = winrt::Windows::UI::Xaml::Window::Current().Content();
        if (!root)
        {
            Log(log, L"The editor XAML tree has no root content.");
            return {};
        }

        std::vector<winrt::Windows::UI::Xaml::DependencyObject> pending{root};
        size_t inspected = 0;
        while (!pending.empty() && inspected < 512)
        {
            const auto node = pending.back();
            pending.pop_back();
            ++inspected;

            if (const auto manifest = FindAvatarManifestInObject(log, node, 0);
                !manifest.empty())
            {
                Log(log, L"Saved avatar discovered directly on an editor control.");
                return manifest;
            }

            if (const auto element =
                    node.try_as<winrt::Windows::UI::Xaml::FrameworkElement>())
            {
                for (const auto& pair : element.Resources())
                {
                    const auto manifest =
                        FindAvatarManifestInObject(log, pair.Value(), 0);
                    if (!manifest.empty())
                    {
                        Log(log, L"Saved avatar discovered in editor resources.");
                        return manifest;
                    }
                }
                const auto dataContext = element.DataContext();
                if (dataContext)
                {
                    const auto boundManifest =
                        FindManifestWithXamlBinding(log, dataContext);
                    if (!boundManifest.empty())
                    {
                        Log(log, L"Saved avatar discovered through editor data binding.");
                        return boundManifest;
                    }
                    const auto manifest = FindAvatarManifestInObject(log, dataContext, 0);
                    if (!manifest.empty())
                    {
                        Log(log, L"Saved avatar discovered through the editor visual tree.");
                        return manifest;
                    }
                }
            }

            const int childCount =
                winrt::Windows::UI::Xaml::Media::VisualTreeHelper::GetChildrenCount(node);
            for (int index = 0; index < childCount; ++index)
            {
                pending.push_back(
                    winrt::Windows::UI::Xaml::Media::VisualTreeHelper::GetChild(node, index));
            }
        }
        Log(log, L"Inspected editor visual nodes: " + std::to_wstring(inspected));
        return {};
    }

    bool IsReadableMemory(DWORD protection)
    {
        if ((protection & (PAGE_GUARD | PAGE_NOACCESS)) != 0)
        {
            return false;
        }
        switch (protection & 0xFF)
        {
        case PAGE_READONLY:
        case PAGE_READWRITE:
        case PAGE_WRITECOPY:
        case PAGE_EXECUTE_READ:
        case PAGE_EXECUTE_READWRITE:
        case PAGE_EXECUTE_WRITECOPY:
            return true;
        default:
            return false;
        }
    }

    bool IsBase64Character(wchar_t value)
    {
        return (value >= L'A' && value <= L'Z') ||
               (value >= L'a' && value <= L'z') ||
               (value >= L'0' && value <= L'9') || value == L'+' ||
               value == L'/' || value == L'=';
    }

    bool IsAvatarManifest(const std::wstring& candidate)
    {
        if (candidate.size() != 1336)
        {
            return false;
        }
        DWORD byteCount = 0;
        if (!CryptStringToBinaryW(
                candidate.c_str(),
                static_cast<DWORD>(candidate.size()),
                CRYPT_STRING_BASE64,
                nullptr,
                &byteCount,
                nullptr,
                nullptr) ||
            byteCount != 1000)
        {
            return false;
        }
        std::vector<unsigned char> bytes(byteCount);
        if (!CryptStringToBinaryW(
                candidate.c_str(),
                static_cast<DWORD>(candidate.size()),
                CRYPT_STRING_BASE64,
                bytes.data(),
                &byteCount,
                nullptr,
                nullptr))
        {
            return false;
        }

        const unsigned char assetTail[] = {0xC1, 0xC8, 0xF1, 0x09, 0xA1, 0x9C, 0xB2, 0xE0};
        return bytes[0x120] == 0x00 && bytes[0x121] == 0x00 &&
               bytes[0x122] == 0x00 && bytes[0x123] == 0x02 &&
               memcmp(bytes.data() + 0x128, assetTail, sizeof(assetTail)) == 0 &&
               bytes[0x140] == 0x00 && bytes[0x141] == 0x00 &&
               bytes[0x142] == 0x00 && bytes[0x143] == 0x01 &&
               memcmp(bytes.data() + 0x148, assetTail, sizeof(assetTail)) == 0;
    }

    bool IsRawAvatarManifest(const unsigned char* bytes, size_t byteCount)
    {
        if (!bytes || byteCount < 1000)
        {
            return false;
        }
        const unsigned char assetTail[] = {0xC1, 0xC8, 0xF1, 0x09, 0xA1, 0x9C, 0xB2, 0xE0};
        return bytes[0x120] == 0x00 && bytes[0x121] == 0x00 &&
               bytes[0x122] == 0x00 && bytes[0x123] == 0x02 &&
               memcmp(bytes + 0x128, assetTail, sizeof(assetTail)) == 0 &&
               bytes[0x140] == 0x00 && bytes[0x141] == 0x00 &&
               bytes[0x142] == 0x00 && bytes[0x143] == 0x01 &&
               memcmp(bytes + 0x148, assetTail, sizeof(assetTail)) == 0;
    }

    std::wstring EncodeManifest(const unsigned char* bytes)
    {
        DWORD characterCount = 0;
        if (!CryptBinaryToStringW(
                bytes,
                1000,
                CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF,
                nullptr,
                &characterCount))
        {
            return {};
        }
        std::wstring result(characterCount, L'\0');
        if (!CryptBinaryToStringW(
                bytes,
                1000,
                CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF,
                result.data(),
                &characterCount))
        {
            return {};
        }
        result.resize(characterCount);
        if (!result.empty() && result.back() == L'\0')
        {
            result.pop_back();
        }
        return result;
    }

    winrt::hstring FindManifestInProcessMemory(std::wofstream& log)
    {
        std::set<std::wstring> candidates;
        SYSTEM_INFO information{};
        GetSystemInfo(&information);
        auto* address = static_cast<unsigned char*>(information.lpMinimumApplicationAddress);
        auto* maximum = static_cast<unsigned char*>(information.lpMaximumApplicationAddress);
        const HANDLE process = GetCurrentProcess();
        constexpr size_t manifestCharacters = 1336;

        while (address < maximum)
        {
            MEMORY_BASIC_INFORMATION region{};
            if (!VirtualQuery(address, &region, sizeof(region)))
            {
                break;
            }
            auto* next = static_cast<unsigned char*>(region.BaseAddress) + region.RegionSize;
            if (region.State == MEM_COMMIT && IsReadableMemory(region.Protect) &&
                region.RegionSize >= manifestCharacters * sizeof(wchar_t))
            {
                constexpr size_t chunkCapacity = 16 * 1024 * 1024;
                constexpr size_t overlap = manifestCharacters * sizeof(wchar_t);
                for (size_t regionOffset = 0; regionOffset < region.RegionSize;)
                {
                    const size_t chunkSize = std::min(
                        chunkCapacity,
                        static_cast<size_t>(region.RegionSize - regionOffset));
                    std::vector<unsigned char> copy(chunkSize);
                    SIZE_T bytesRead = 0;
                    if (!ReadProcessMemory(
                            process,
                            static_cast<unsigned char*>(region.BaseAddress) + regionOffset,
                            copy.data(),
                            copy.size(),
                            &bytesRead))
                    {
                        regionOffset += chunkSize;
                        continue;
                    }
                    for (size_t endOffset =
                             (manifestCharacters - 2) * sizeof(wchar_t);
                         endOffset + 2 * sizeof(wchar_t) <= bytesRead;
                         endOffset += sizeof(wchar_t))
                    {
                        const auto* ending =
                            reinterpret_cast<const wchar_t*>(copy.data() + endOffset);
                        if (ending[0] != L'=' || ending[1] != L'=')
                        {
                            continue;
                        }
                        const size_t offset =
                            endOffset - (manifestCharacters - 2) * sizeof(wchar_t);
                        const auto* text =
                            reinterpret_cast<const wchar_t*>(copy.data() + offset);
                        bool base64 = true;
                        for (size_t index = 0; index < manifestCharacters; ++index)
                        {
                            if (!IsBase64Character(text[index]))
                            {
                                base64 = false;
                                break;
                            }
                        }
                        if (!base64)
                        {
                            continue;
                        }
                        const std::wstring candidate(text, manifestCharacters);
                        if (IsAvatarManifest(candidate))
                        {
                            candidates.insert(candidate);
                        }
                    }
                    for (size_t offset = 0; offset + 1000 <= bytesRead; ++offset)
                    {
                        const auto* raw = copy.data() + offset;
                        if (IsRawAvatarManifest(raw, bytesRead - offset))
                        {
                            const auto candidate = EncodeManifest(raw);
                            if (candidate.size() == manifestCharacters)
                            {
                                candidates.insert(candidate);
                            }
                            offset += 999;
                        }
                    }
                    if (regionOffset + chunkSize >= region.RegionSize)
                    {
                        break;
                    }
                    regionOffset += chunkSize - overlap;
                }
            }
            if (next <= address)
            {
                break;
            }
            address = next;
        }

        Log(log, L"Structurally valid manifests found in editor memory: " +
                     std::to_wstring(candidates.size()));
        if (candidates.empty())
        {
            return {};
        }

        std::wstring selected;
        unsigned int selectedScore = 0;
        size_t candidateIndex = 0;
        for (const auto& candidate : candidates)
        {
            ++candidateIndex;
            const auto assets = ManifestAssets(winrt::hstring(candidate));
            unsigned int score = 0;
            std::wstring description = L"Manifest candidate " +
                                       std::to_wstring(candidateIndex) + L" assets:";
            for (const auto& [mask, assetId] : assets)
            {
                description += L" " + assetId;
                const unsigned int bitCount = std::popcount(mask);
                if (bitCount > 1)
                {
                    score += bitCount * 10;
                }
                if (mask == 0x00000FFC || mask == 0x00800218)
                {
                    score += 500;
                }
            }
            Log(log, description + L" score=" + std::to_wstring(score));
            if (selected.empty() || score > selectedScore)
            {
                selected = candidate;
                selectedScore = score;
            }
        }
        Log(log, L"Selected in-memory manifest score: " +
                     std::to_wstring(selectedScore));
        return winrt::hstring(selected);
    }

    bool ApplyExporterPathCompatibilityPatch(std::wofstream& log)
    {
        HMODULE module = GetModuleHandleW(L"Avatars.FoundationPrivate.dll");
        if (!module)
        {
            Log(log, L"Exporter compatibility patch skipped: avatar component is not loaded.");
            return false;
        }

        auto patchBytes = [&log](
                              unsigned char* address,
                              const unsigned char* expected,
                              const unsigned char* replacement,
                              size_t size,
                              const wchar_t* name) -> bool
        {
            if (memcmp(address, replacement, size) == 0)
            {
                Log(log, std::wstring(name) + L" is already active.");
                return true;
            }
            if (memcmp(address, expected, size) != 0)
            {
                Log(log, std::wstring(name) +
                             L" skipped: installed component bytes do not match.");
                return false;
            }

            DWORD previousProtection = 0;
            if (!VirtualProtect(address, size, PAGE_EXECUTE_READWRITE, &previousProtection))
            {
                Log(log, std::wstring(name) +
                             L" failed: VirtualProtect rejected the change.");
                return false;
            }
            memcpy(address, replacement, size);
            FlushInstructionCache(GetCurrentProcess(), address, size);
            DWORD ignored = 0;
            VirtualProtect(address, size, previousProtection, &ignored);
            Log(log, std::wstring(L"Applied ") + name + L".");
            return true;
        };

        auto* folderInstruction = reinterpret_cast<unsigned char*>(module) + 0x4544F;
        const unsigned char expectedFolderInstruction[] = {0x49, 0x8B, 0xF8};
        const unsigned char folderReplacement[] = {0x31, 0xFF, 0x90};
        if (!patchBytes(
                folderInstruction,
                expectedFolderInstruction,
                folderReplacement,
                sizeof(folderReplacement),
                L"Windows 11 exporter folder patch"))
        {
            return false;
        }

        auto* separatorInstruction = reinterpret_cast<unsigned char*>(module) + 0x454A1;
        const unsigned char expectedSeparatorInstruction[] = {
            0xBA, 0x01, 0x00, 0x00, 0x00,
            0x48, 0x8D, 0x0D, 0x47, 0xC0, 0x0D, 0x00};
        const unsigned char separatorReplacement[] = {
            0xBA, 0x00, 0x00, 0x00, 0x00,
            0x31, 0xC9, 0x90, 0x90, 0x90, 0x90, 0x90};
        if (!patchBytes(
                separatorInstruction,
                expectedSeparatorInstruction,
                separatorReplacement,
                sizeof(separatorReplacement),
                L"Windows 11 exporter separator patch"))
        {
            return false;
        }

        // Microsoft's Babylon writer deliberately filters model types for each
        // asset category.  That is useful for its internal tooling, but drops
        // equipped hair/clothing/accessory meshes from a general avatar export.
        // Keep the writer and texture serialization intact while allowing every
        // model already present in the selected asset's collection to pass.
        auto* modelFilter = reinterpret_cast<unsigned char*>(module) + 0x44B57;
        const unsigned char expectedModelFilter[] = {
            0x84, 0xD2,
            0x0F, 0x84, 0xF3, 0x00, 0x00, 0x00,
            0x84, 0xC9,
            0x0F, 0x85, 0xEB, 0x00, 0x00, 0x00,
            0x84, 0xC0,
            0x0F, 0x85, 0xE3, 0x00, 0x00, 0x00};
        unsigned char modelFilterReplacement[sizeof(expectedModelFilter)];
        memset(modelFilterReplacement, 0x90, sizeof(modelFilterReplacement));
        if (!patchBytes(
                modelFilter,
                expectedModelFilter,
                modelFilterReplacement,
                sizeof(modelFilterReplacement),
                L"complete equipped-model export patch"))
        {
            return false;
        }

        // The next writer gate applies the same category filter to textures.
        // With every equipped model now serialized, allow each one's material
        // textures to be emitted beside it as well.
        auto* textureFilter = reinterpret_cast<unsigned char*>(module) + 0x44BD8;
        const unsigned char expectedTextureFilter[] = {0x74, 0x44};
        const unsigned char textureFilterReplacement[] = {0x90, 0x90};
        if (!patchBytes(
                textureFilter,
                expectedTextureFilter,
                textureFilterReplacement,
                sizeof(textureFilterReplacement),
                L"complete equipped-texture export patch"))
        {
            return false;
        }
        return true;
    }

    DWORD WINAPI BridgeMain(void*)
    {
        BridgeRunReset runReset;
        const fs::path localState = LocalStatePath();
        std::error_code error;
        fs::create_directories(localState, error);

        std::wofstream log(localState / L"AvatarBridge.log", std::ios::trunc);
        Log(log, L"Avatar bridge started inside Xbox Original Avatars.");

        try
        {
            winrt::init_apartment(winrt::apartment_type::multi_threaded);
            Log(log, L"WinRT apartment initialized.");

            const auto settings =
                winrt::Windows::Storage::ApplicationData::Current().LocalSettings().Values();
            Log(log, L"Local settings count: " + std::to_wstring(settings.Size()));
            for (const auto& pair : settings)
            {
                std::wstring description = L"Setting: " + std::wstring(pair.Key().c_str());
                if (const auto property =
                        pair.Value().try_as<winrt::Windows::Foundation::IPropertyValue>())
                {
                    description += L" type=" +
                                   std::to_wstring(static_cast<int>(property.Type()));
                    if (property.Type() ==
                        winrt::Windows::Foundation::PropertyType::String)
                    {
                        description += L" stringLength=" +
                                       std::to_wstring(property.GetString().size());
                    }
                }
                Log(log, description);
            }

            const auto dispatcher =
                winrt::Windows::ApplicationModel::Core::CoreApplication::MainView()
                    .CoreWindow()
                    .Dispatcher();
            Log(log, L"Main XAML dispatcher acquired.");
            if (!ApplyExporterPathCompatibilityPatch(log))
            {
                return 1;
            }

            const std::wstring exportRelativeFolder = L"AvatarExport";
            const fs::path temporaryFolder =
                fs::path(winrt::Windows::Storage::ApplicationData::Current()
                             .TemporaryFolder()
                             .Path()
                             .c_str());
            const fs::path exportFolder = temporaryFolder / exportRelativeFolder;
            for (const auto& entry : fs::directory_iterator(temporaryFolder, error))
            {
                const std::wstring name = entry.path().filename().wstring();
                if (entry.is_regular_file() &&
                    (name.starts_with(L"avatar-selected-") ||
                     name == L"avatar-poses.txt"))
                {
                    fs::remove(entry.path(), error);
                    error.clear();
                }
            }
            fs::remove_all(exportFolder, error);
            error.clear();
            fs::create_directories(exportFolder, error);
            fs::create_directories(exportFolder / L"textures", error);
            Log(log, L"Exporter output folder: " + exportFolder.wstring());
            avatar::Adapter::FoundationAdapter adapter{nullptr};
            avatar::API::IFoundationProxy proxy{nullptr};
            avatar::API::IAvatarScene scene{nullptr};
            winrt::Windows::Foundation::IAsyncOperation<avatar::API::IAvatar>
                createAvatarOperation{nullptr};
            avatar::API::IAvatar createdAvatar{nullptr};
            winrt::Windows::Foundation::IAsyncAction poseAction{nullptr};
            winrt::hstring manifest;
            HANDLE avatarLoadedEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!avatarLoadedEvent)
            {
                Log(log, L"Could not create the avatar-loaded synchronization event.");
                return 1;
            }
            winrt::event_token avatarLoadedToken{};
            std::wstring uiFailure;
            dispatcher.RunAsync(
                winrt::Windows::UI::Core::CoreDispatcherPriority::Normal,
                [&log,
                 &uiFailure,
                 &adapter,
                 &proxy,
                 &scene,
                 &createAvatarOperation,
                 &manifest]()
                {
                    try
                    {
                        adapter = avatar::Adapter::FoundationAdapter();
                        Log(log, L"FoundationAdapter activated on the UI thread.");

                        const auto deviceSettings =
                            avatar::Adapter::FoundationAdapter::CreateDefaultDeviceSettings(
                                avatar::API::RenderingQuality::Hq, 0);
                        const auto coreSettings =
                            avatar::Adapter::FoundationAdapter::CreateDefaultCore2Settings();
                        const winrt::Windows::UI::Xaml::Controls::SwapChainPanel panel{nullptr};
                        adapter.Initialize(
                            deviceSettings,
                            coreSettings,
                            panel,
                            winrt::Windows::Foundation::Size{128.0f, 128.0f});
                        Log(log, L"Headless FoundationAdapter initialization succeeded.");

                        proxy = adapter.CoreProxy();
                        Log(log, proxy ? L"CoreProxy acquired after initialization."
                                       : L"CoreProxy is null after initialization.");
                        if (proxy)
                        {
                            proxy.StartRendering();
                            Log(log, L"Headless render/update loop started.");
                            scene = proxy.Scene();
                            Log(log, scene ? L"Scene acquired." : L"Scene is null.");

                            if (scene)
                            {
                                manifest = FindEditorAvatarManifest(log);
                                if (manifest.empty())
                                {
                                    manifest = FindManifestInProcessMemory(log);
                                }
                                const auto currentAvatar = scene.CurrentAvatar();
                                Log(log, currentAvatar ? L"Scene has a current avatar."
                                                       : L"Scene has no current avatar.");

                                if (!currentAvatar)
                                {
                                    if (!manifest.empty())
                                    {
                                        createAvatarOperation =
                                            scene.CreateAvatarFromManifestAsync(manifest);
                                        Log(log, L"Saved editor avatar creation started.");
                                    }
                                    else
                                    {
                                        createAvatarOperation = scene.CreateRandomAvatarAsync(
                                            avatar::API::GenderType::Male);
                                        Log(log, L"Saved avatar was unavailable; random validation avatar started.");
                                    }
                                }
                            }
                        }
                    }
                    catch (const winrt::hresult_error& exception)
                    {
                        uiFailure = std::wstring(L"UI-thread WinRT failure: ") +
                                    winrt::to_hstring(exception.code().value).c_str() + L" " +
                                    std::wstring(exception.message().c_str());
                    }
                    catch (const std::exception& exception)
                    {
                        uiFailure = std::wstring(L"UI-thread C++ failure: ") +
                                    winrt::to_hstring(exception.what()).c_str();
                    }
                    catch (...)
                    {
                        uiFailure = L"Unknown UI-thread failure.";
                    }
                }).get();

            if (!uiFailure.empty())
            {
                Log(log, uiFailure);
            }
            else if (createAvatarOperation)
            {
                createdAvatar = createAvatarOperation.get();
                Log(log, createdAvatar ? L"Selected avatar creation completed."
                                       : L"Selected avatar creation returned null.");

                dispatcher.RunAsync(
                    winrt::Windows::UI::Core::CoreDispatcherPriority::Normal,
                    [&log,
                     &uiFailure,
                     &scene,
                     &createdAvatar,
                     &manifest,
                     avatarLoadedEvent,
                     &avatarLoadedToken]()
                    {
                        try
                        {
                            avatarLoadedToken = createdAvatar.AvatarLoaded(
                                [avatarLoadedEvent](const auto&, const auto&)
                                {
                                    SetEvent(avatarLoadedEvent);
                                });
                            scene.PlaceAvatar(createdAvatar);
                            Log(log, L"Selected avatar placed in the export scene.");
                            if (createdAvatar.IsLoaded())
                            {
                                SetEvent(avatarLoadedEvent);
                            }
                            manifest = createdAvatar.Manifest();
                            Log(log, L"Selected avatar manifest acquired; length=" +
                                     std::to_wstring(manifest.size()));
                        }
                        catch (const winrt::hresult_error& exception)
                        {
                            uiFailure = std::wstring(L"Second UI-thread WinRT failure: ") +
                                        winrt::to_hstring(exception.code().value).c_str() + L" " +
                                        std::wstring(exception.message().c_str());
                        }
                        catch (const std::exception& exception)
                        {
                            uiFailure = std::wstring(L"Second UI-thread C++ failure: ") +
                                        winrt::to_hstring(exception.what()).c_str();
                        }
                    }).get();

                if (!manifest.empty())
                {
                    std::ofstream manifestFile(
                        exportFolder / L"RandomAvatarManifest.base64", std::ios::trunc);
                    manifestFile << winrt::to_string(manifest);
                    Log(log, L"Selected avatar manifest saved.");
                }

                if (uiFailure.empty())
                {
                    const DWORD loaded = WaitForSingleObject(avatarLoadedEvent, 45000);
                    if (loaded == WAIT_OBJECT_0)
                    {
                        Log(log, L"Selected avatar models finished loading.");
                    }
                    else
                    {
                        uiFailure = L"Timed out waiting for selected avatar models to load.";
                    }
                }

                if (uiFailure.empty())
                {
                    dispatcher.RunAsync(
                        winrt::Windows::UI::Core::CoreDispatcherPriority::Normal,
                        [&log,
                         &uiFailure,
                         &proxy,
                         &poseAction]()
                        {
                            try
                            {
                                poseAction = proxy.ExportPoses_PRIVATEAPI(
                                    L"", L"avatar-poses");
                                Log(log, L"Pose export started.");
                            }
                            catch (const winrt::hresult_error& exception)
                            {
                                uiFailure = std::wstring(L"Third UI-thread WinRT failure: ") +
                                            winrt::to_hstring(exception.code().value).c_str() +
                                            L" " + std::wstring(exception.message().c_str());
                            }
                        }).get();
                }

                if (uiFailure.empty() && poseAction)
                {
                    poseAction.get();
                    Log(log, L"Pose export completed.");

                    struct ExportTarget
                    {
                        std::wstring name;
                        std::wstring assetId;
                    };
                    std::vector<ExportTarget> targets;
                    for (const auto& [mask, assetId] : ManifestAssets(manifest))
                    {
                        wchar_t maskName[9]{};
                        swprintf_s(maskName, L"%08x", mask);
                        targets.push_back({maskName, assetId});
                    }

                    for (const auto& target : targets)
                    {
                        const std::wstring& assetId = target.assetId;
                        if (assetId.empty() || assetId == L"00000000-0000-0000-0000-000000000000")
                        {
                            Log(log, L"Category is not equipped: " + target.name);
                            continue;
                        }

                        fs::create_directories(exportFolder / assetId / L"textures", error);

                        winrt::Windows::Foundation::IAsyncAction categoryAction{nullptr};
                        dispatcher.RunAsync(
                            winrt::Windows::UI::Core::CoreDispatcherPriority::Normal,
                            [&log,
                             &uiFailure,
                             &proxy,
                             &categoryAction,
                             &target,
                             &assetId,
                             &exportRelativeFolder]()
                            {
                                try
                                {
                                    const std::wstring fileName =
                                        std::wstring(L"avatar-selected-") + target.name;
                                    categoryAction = proxy.ExportAssets_PRIVATEAPI(
                                        exportRelativeFolder,
                                        fileName,
                                        assetId,
                                        true,
                                        true);
                                    Log(log, std::wstring(L"Category export started: ") +
                                                 target.name + L" " + assetId);
                                }
                                catch (const winrt::hresult_error& exception)
                                {
                                    uiFailure = std::wstring(L"Category export start failed: ") +
                                                target.name + L" " +
                                                winrt::to_hstring(exception.code().value).c_str() +
                                                L" " + std::wstring(exception.message().c_str());
                                }
                            }).get();

                        if (!uiFailure.empty() || !categoryAction)
                        {
                            break;
                        }
                        try
                        {
                            categoryAction.get();
                            Log(log, std::wstring(L"Category export completed: ") + target.name);
                        }
                        catch (const winrt::hresult_error& exception)
                        {
                            Log(log, std::wstring(L"Category export failed: ") + target.name +
                                         L" " +
                                         winrt::to_hstring(exception.code().value).c_str() + L" " +
                                         std::wstring(exception.message().c_str()));
                            uiFailure.clear();
                        }
                    }
                }
                else if (!uiFailure.empty())
                {
                    Log(log, uiFailure);
                }

                if (avatarLoadedToken.value)
                {
                    dispatcher.RunAsync(
                        winrt::Windows::UI::Core::CoreDispatcherPriority::Normal,
                        [&createdAvatar, avatarLoadedToken]()
                        {
                            createdAvatar.AvatarLoaded(avatarLoadedToken);
                        }).get();
                }
                CloseHandle(avatarLoadedEvent);

                for (const auto& entry : fs::directory_iterator(exportFolder, error))
                {
                    if (!error)
                    {
                        Log(log, L"Exported: " + entry.path().filename().wstring());
                    }
                }
            }

            Log(log, L"Bridge activation probe completed.");
            winrt::uninit_apartment();
            return 0;
        }
        catch (const winrt::hresult_error& exception)
        {
            Log(log, std::wstring(L"WinRT failure: ") +
                     winrt::to_hstring(exception.code().value).c_str() + L" " +
                     std::wstring(exception.message().c_str()));
        }
        catch (const std::exception& exception)
        {
            Log(log, std::wstring(L"C++ failure: ") +
                     winrt::to_hstring(exception.what()).c_str());
        }
        catch (...)
        {
            Log(log, L"Unknown bridge failure.");
        }
        return 1;
    }
}

extern "C" __declspec(dllexport) DWORD WINAPI StartAvatarExport(void*)
{
    if (InterlockedCompareExchange(&g_bridgeRunning, 1, 0) != 0)
    {
        return 1;
    }
    if (HANDLE thread = CreateThread(nullptr, 0, BridgeMain, nullptr, 0, nullptr))
    {
        CloseHandle(thread);
        return 1;
    }
    InterlockedExchange(&g_bridgeRunning, 0);
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, void*)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(instance);
        StartAvatarExport(nullptr);
    }
    return TRUE;
}
