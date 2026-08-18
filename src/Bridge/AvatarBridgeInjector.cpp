#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>

#include <filesystem>
#include <iostream>
#include <string>

namespace fs = std::filesystem;

namespace
{
    std::wstring ErrorMessage(DWORD value)
    {
        wchar_t* buffer = nullptr;
        const DWORD length = FormatMessageW(
            FORMAT_MESSAGE_ALLOCATE_BUFFER | FORMAT_MESSAGE_FROM_SYSTEM |
                FORMAT_MESSAGE_IGNORE_INSERTS,
            nullptr,
            value,
            0,
            reinterpret_cast<wchar_t*>(&buffer),
            0,
            nullptr);
        std::wstring result = length && buffer ? buffer : L"Unknown error";
        if (buffer)
        {
            LocalFree(buffer);
        }
        return result;
    }

    int Fail(const wchar_t* operation)
    {
        const DWORD value = GetLastError();
        std::wcerr << operation << L" failed (" << value << L"): "
                   << ErrorMessage(value) << L'\n';
        return 1;
    }

    uintptr_t FindRemoteModuleBase(DWORD processId, const fs::path& dllPath)
    {
        HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, processId);
        if (snapshot == INVALID_HANDLE_VALUE)
        {
            return 0;
        }
        MODULEENTRY32W entry{};
        entry.dwSize = sizeof(entry);
        uintptr_t result = 0;
        if (Module32FirstW(snapshot, &entry))
        {
            do
            {
                std::error_code error;
                if (fs::equivalent(dllPath, fs::path(entry.szExePath), error) ||
                    _wcsicmp(dllPath.filename().c_str(), entry.szModule) == 0)
                {
                    result = reinterpret_cast<uintptr_t>(entry.modBaseAddr);
                    break;
                }
            } while (Module32NextW(snapshot, &entry));
        }
        CloseHandle(snapshot);
        return result;
    }
}

int wmain(int argc, wchar_t* argv[])
{
    if (argc != 3)
    {
        std::wcerr << L"Usage: AvatarBridgeInjector.exe <process-id> <bridge-dll>\n";
        return 2;
    }

    wchar_t* end = nullptr;
    const unsigned long processId = wcstoul(argv[1], &end, 10);
    if (!processId || !end || *end)
    {
        std::wcerr << L"Invalid process ID.\n";
        return 2;
    }

    const fs::path dllPath = fs::absolute(argv[2]);
    if (!fs::is_regular_file(dllPath))
    {
        std::wcerr << L"Bridge DLL not found: " << dllPath << L'\n';
        return 2;
    }

    HANDLE process = OpenProcess(
        PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
            PROCESS_VM_WRITE | PROCESS_VM_READ,
        FALSE,
        processId);
    if (!process)
    {
        return Fail(L"OpenProcess");
    }

    const std::wstring pathText = dllPath.wstring();
    const SIZE_T pathBytes = (pathText.size() + 1) * sizeof(wchar_t);
    void* remotePath = VirtualAllocEx(
        process, nullptr, pathBytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!remotePath)
    {
        CloseHandle(process);
        return Fail(L"VirtualAllocEx");
    }

    SIZE_T bytesWritten = 0;
    if (!WriteProcessMemory(
            process, remotePath, pathText.c_str(), pathBytes, &bytesWritten) ||
        bytesWritten != pathBytes)
    {
        VirtualFreeEx(process, remotePath, 0, MEM_RELEASE);
        CloseHandle(process);
        return Fail(L"WriteProcessMemory");
    }

    HMODULE kernel32 = GetModuleHandleW(L"kernel32.dll");
    auto loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(
        GetProcAddress(kernel32, "LoadLibraryW"));
    if (!loadLibrary)
    {
        VirtualFreeEx(process, remotePath, 0, MEM_RELEASE);
        CloseHandle(process);
        return Fail(L"GetProcAddress(LoadLibraryW)");
    }

    HANDLE thread = CreateRemoteThread(
        process, nullptr, 0, loadLibrary, remotePath, 0, nullptr);
    if (!thread)
    {
        VirtualFreeEx(process, remotePath, 0, MEM_RELEASE);
        CloseHandle(process);
        return Fail(L"CreateRemoteThread");
    }

    const DWORD wait = WaitForSingleObject(thread, 15000);
    DWORD result = 0;
    if (wait != WAIT_OBJECT_0 || !GetExitCodeThread(thread, &result))
    {
        CloseHandle(thread);
        VirtualFreeEx(process, remotePath, 0, MEM_RELEASE);
        CloseHandle(process);
        return Fail(L"Waiting for LoadLibraryW");
    }

    CloseHandle(thread);
    VirtualFreeEx(process, remotePath, 0, MEM_RELEASE);

    if (!result)
    {
        CloseHandle(process);
        std::wcerr << L"LoadLibraryW was rejected by the target process.\n";
        return 1;
    }

    uintptr_t remoteBase = 0;
    for (int attempt = 0; attempt < 50 && !remoteBase; ++attempt)
    {
        remoteBase = FindRemoteModuleBase(processId, dllPath);
        if (!remoteBase)
        {
            Sleep(100);
        }
    }
    HMODULE localModule = LoadLibraryExW(dllPath.c_str(), nullptr, DONT_RESOLVE_DLL_REFERENCES);
    if (!remoteBase || !localModule)
    {
        CloseHandle(process);
        return Fail(L"Locating the bridge module");
    }
    FARPROC localStart = GetProcAddress(localModule, "StartOpenClassicAvatarExport");
    if (!localStart)
    {
        FreeLibrary(localModule);
        CloseHandle(process);
        return Fail(L"GetProcAddress(StartOpenClassicAvatarExport)");
    }
    const uintptr_t startRva =
        reinterpret_cast<uintptr_t>(localStart) - reinterpret_cast<uintptr_t>(localModule);
    FreeLibrary(localModule);

    HANDLE startThread = CreateRemoteThread(
        process,
        nullptr,
        0,
        reinterpret_cast<LPTHREAD_START_ROUTINE>(remoteBase + startRva),
        nullptr,
        0,
        nullptr);
    if (!startThread)
    {
        CloseHandle(process);
        return Fail(L"Starting the avatar export");
    }
    if (WaitForSingleObject(startThread, 15000) != WAIT_OBJECT_0)
    {
        CloseHandle(startThread);
        CloseHandle(process);
        return Fail(L"Waiting for the avatar export starter");
    }
    CloseHandle(startThread);
    CloseHandle(process);

    std::wcout << L"Avatar export started in process " << processId << L".\n";
    return 0;
}
