#include <stdint.h>
#include <limits.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <wchar.h>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#else
#include <errno.h>
#include <fcntl.h>
#include <dirent.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
#if defined(__APPLE__)
#include <mach-o/dyld.h>
#endif
#endif

enum
{
    CAMP_IO_OK = 0,
    CAMP_IO_UNKNOWN,
    CAMP_IO_INVALID_ARGUMENT,
    CAMP_IO_NOT_SUPPORTED,
    CAMP_IO_NOT_FOUND,
    CAMP_IO_ALREADY_EXISTS,
    CAMP_IO_PERMISSION_DENIED,
    CAMP_IO_BUSY,
    CAMP_IO_INTERRUPTED,
    CAMP_IO_WOULD_BLOCK,
    CAMP_IO_TOO_MANY_OPEN_FILES,
    CAMP_IO_PATH_TOO_LONG,
    CAMP_IO_NO_SPACE,
    CAMP_IO_READ_ONLY,
    CAMP_IO_IS_DIRECTORY,
    CAMP_IO_NOT_DIRECTORY,
    CAMP_IO_DIRECTORY_NOT_EMPTY,
    CAMP_IO_BROKEN_PIPE,
    CAMP_IO_IO,
    CAMP_IO_NO_MEMORY,
    CAMP_IO_TIMEOUT
};

enum
{
    CAMP_IO_ACCESS_READ = 1,
    CAMP_IO_ACCESS_WRITE = 2,
    CAMP_IO_ACCESS_READ_WRITE = 3
};

enum
{
    CAMP_IO_MODE_OPEN_EXISTING = 0,
    CAMP_IO_MODE_CREATE = 1,
    CAMP_IO_MODE_CREATE_OR_TRUNCATE = 2,
    CAMP_IO_MODE_APPEND = 3
};

enum
{
    CAMP_IO_SEEK_BEGIN = 0,
    CAMP_IO_SEEK_CURRENT = 1,
    CAMP_IO_SEEK_END = 2
};

enum
{
    CAMP_IO_OPTION_SEQUENTIAL = 1,
    CAMP_IO_OPTION_RANDOM_ACCESS = 2,
    CAMP_IO_OPTION_WRITE_THROUGH = 4,
    CAMP_IO_OPTION_SHARE_READ = 8,
    CAMP_IO_OPTION_SHARE_WRITE = 16
};

#define CAMP_IO_INVALID_HANDLE ((intptr_t)-1)

static void camp_io_set_error(int *error, int value)
{
    if (error != NULL)
        *error = value;
}

static intptr_t camp_io_copy_utf8_result(const char *value, uintptr_t value_length, char *buffer, uintptr_t length)
{
    if (buffer != NULL && length > 0)
    {
        uintptr_t copied = value_length;
        if (copied > length)
            copied = length;
        if (copied > 0)
            memcpy(buffer, value, (size_t)copied);
        if (copied < length)
            buffer[copied] = '\0';
    }
    return (intptr_t)value_length;
}

#if defined(_WIN32)

static int camp_io_error_from_windows(DWORD error)
{
    switch (error)
    {
        case ERROR_SUCCESS:
            return CAMP_IO_OK;
        case ERROR_FILE_NOT_FOUND:
        case ERROR_PATH_NOT_FOUND:
            return CAMP_IO_NOT_FOUND;
        case ERROR_FILE_EXISTS:
        case ERROR_ALREADY_EXISTS:
            return CAMP_IO_ALREADY_EXISTS;
        case ERROR_ACCESS_DENIED:
        case ERROR_SHARING_VIOLATION:
            return CAMP_IO_PERMISSION_DENIED;
        case ERROR_BUSY:
        case ERROR_LOCK_VIOLATION:
            return CAMP_IO_BUSY;
        case ERROR_TOO_MANY_OPEN_FILES:
            return CAMP_IO_TOO_MANY_OPEN_FILES;
        case ERROR_FILENAME_EXCED_RANGE:
            return CAMP_IO_PATH_TOO_LONG;
        case ERROR_DISK_FULL:
#ifdef ERROR_HANDLE_DISK_FULL
        case ERROR_HANDLE_DISK_FULL:
#endif
            return CAMP_IO_NO_SPACE;
        case ERROR_WRITE_PROTECT:
            return CAMP_IO_READ_ONLY;
        case ERROR_DIRECTORY:
            return CAMP_IO_NOT_DIRECTORY;
        case ERROR_DIR_NOT_EMPTY:
            return CAMP_IO_DIRECTORY_NOT_EMPTY;
        case ERROR_BROKEN_PIPE:
        case ERROR_NO_DATA:
            return CAMP_IO_BROKEN_PIPE;
        case ERROR_NOT_ENOUGH_MEMORY:
        case ERROR_OUTOFMEMORY:
            return CAMP_IO_NO_MEMORY;
        case ERROR_INVALID_PARAMETER:
        case ERROR_INVALID_HANDLE:
            return CAMP_IO_INVALID_ARGUMENT;
        case ERROR_OPERATION_ABORTED:
            return CAMP_IO_INTERRUPTED;
        case ERROR_NOT_SUPPORTED:
            return CAMP_IO_NOT_SUPPORTED;
#ifdef ERROR_SEM_TIMEOUT
        case ERROR_SEM_TIMEOUT:
#endif
#ifdef ERROR_TIMEOUT
        case ERROR_TIMEOUT:
#endif
            return CAMP_IO_TIMEOUT;
        case ERROR_IO_DEVICE:
        case ERROR_CRC:
            return CAMP_IO_IO;
        default:
            return CAMP_IO_UNKNOWN;
    }
}

static wchar_t *camp_io_utf8_to_wide(const char *path, int *error)
{
    int required;
    wchar_t *wide;

    if (path == NULL)
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return NULL;
    }
    required = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, NULL, 0);
    if (required <= 0)
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        return NULL;
    }

    wide = (wchar_t *)malloc((size_t)required * sizeof(wchar_t));
    if (wide == NULL)
    {
        camp_io_set_error(error, CAMP_IO_NO_MEMORY);
        return NULL;
    }

    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, wide, required) <= 0)
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        free(wide);
        return NULL;
    }
    return wide;
}

static intptr_t camp_io_wide_to_utf8_buffer(const wchar_t *value, char *buffer, uintptr_t length)
{
    int required;
    int copied;
    int source_length;

    if (value == NULL)
        return 0;

    source_length = (int)wcslen(value);
    required = WideCharToMultiByte(CP_UTF8, 0, value, source_length, NULL, 0, NULL, NULL);
    if (required < 0)
        return 0;

    if (buffer != NULL && length > 0)
    {
        if (length > (uintptr_t)INT_MAX)
            length = (uintptr_t)INT_MAX;
        uintptr_t output_length = (uintptr_t)required;
        if (output_length > length)
            output_length = length;
        copied = WideCharToMultiByte(CP_UTF8, 0, value, source_length, buffer, (int)output_length, NULL, NULL);
        if (copied < 0)
        {
            if (length > 0)
                buffer[0] = '\0';
            return (intptr_t)required;
        }
        if ((uintptr_t)copied < length)
            buffer[copied] = '\0';
    }

    return (intptr_t)required;
}

static int camp_io_valid_env_name_wide(const wchar_t *name)
{
    const wchar_t *current;
    if (name == NULL || name[0] == L'\0')
        return 0;
    for (current = name; *current != L'\0'; current++)
        if (*current == L'=')
            return 0;
    return 1;
}

intptr_t camp_io_stdin(void)
{
    HANDLE handle = GetStdHandle(STD_INPUT_HANDLE);
    return handle == INVALID_HANDLE_VALUE || handle == NULL ? CAMP_IO_INVALID_HANDLE : (intptr_t)handle;
}

intptr_t camp_io_stdout(void)
{
    HANDLE handle = GetStdHandle(STD_OUTPUT_HANDLE);
    return handle == INVALID_HANDLE_VALUE || handle == NULL ? CAMP_IO_INVALID_HANDLE : (intptr_t)handle;
}

intptr_t camp_io_stderr(void)
{
    HANDLE handle = GetStdHandle(STD_ERROR_HANDLE);
    return handle == INVALID_HANDLE_VALUE || handle == NULL ? CAMP_IO_INVALID_HANDLE : (intptr_t)handle;
}

intptr_t camp_io_file_open(const char *path, int access, int mode, int options, int *error)
{
    DWORD desired_access = 0;
    DWORD share_mode = 0;
    DWORD creation = OPEN_EXISTING;
    DWORD flags = FILE_ATTRIBUTE_NORMAL;
    HANDLE handle;
    wchar_t *wide;

    if (access == CAMP_IO_ACCESS_READ)
        desired_access = GENERIC_READ;
    else if (access == CAMP_IO_ACCESS_WRITE)
        desired_access = GENERIC_WRITE;
    else if (access == CAMP_IO_ACCESS_READ_WRITE)
        desired_access = GENERIC_READ | GENERIC_WRITE;
    else
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return CAMP_IO_INVALID_HANDLE;
    }

    if ((options & CAMP_IO_OPTION_SHARE_READ) != 0)
        share_mode |= FILE_SHARE_READ;
    if ((options & CAMP_IO_OPTION_SHARE_WRITE) != 0)
        share_mode |= FILE_SHARE_WRITE;
    if ((options & CAMP_IO_OPTION_SEQUENTIAL) != 0)
        flags |= FILE_FLAG_SEQUENTIAL_SCAN;
    if ((options & CAMP_IO_OPTION_RANDOM_ACCESS) != 0)
        flags |= FILE_FLAG_RANDOM_ACCESS;
    if ((options & CAMP_IO_OPTION_WRITE_THROUGH) != 0)
        flags |= FILE_FLAG_WRITE_THROUGH;

    if (mode == CAMP_IO_MODE_OPEN_EXISTING)
        creation = OPEN_EXISTING;
    else if (mode == CAMP_IO_MODE_CREATE)
        creation = CREATE_NEW;
    else if (mode == CAMP_IO_MODE_CREATE_OR_TRUNCATE)
        creation = CREATE_ALWAYS;
    else if (mode == CAMP_IO_MODE_APPEND)
    {
        creation = OPEN_ALWAYS;
        desired_access |= FILE_APPEND_DATA;
    }
    else
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return CAMP_IO_INVALID_HANDLE;
    }

    wide = camp_io_utf8_to_wide(path, error);
    if (wide == NULL)
        return CAMP_IO_INVALID_HANDLE;

    handle = CreateFileW(wide, desired_access, share_mode, NULL, creation, flags, NULL);
    free(wide);
    if (handle == INVALID_HANDLE_VALUE)
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        return CAMP_IO_INVALID_HANDLE;
    }

    if (mode == CAMP_IO_MODE_APPEND)
    {
        LARGE_INTEGER zero;
        zero.QuadPart = 0;
        if (!SetFilePointerEx(handle, zero, NULL, FILE_END))
        {
            camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
            CloseHandle(handle);
            return CAMP_IO_INVALID_HANDLE;
        }
    }

    camp_io_set_error(error, CAMP_IO_OK);
    return (intptr_t)handle;
}

int camp_io_file_close(intptr_t handle, int *error)
{
    if (handle == CAMP_IO_INVALID_HANDLE || handle == 0)
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return 0;
    }
    if (!CloseHandle((HANDLE)handle))
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        return 0;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

intptr_t camp_io_file_read(intptr_t handle, void *buffer, uintptr_t length, int *error)
{
    DWORD done = 0;
    if (length > (uintptr_t)UINT32_MAX)
        length = (uintptr_t)UINT32_MAX;
    if (!ReadFile((HANDLE)handle, buffer, (DWORD)length, &done, NULL))
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        return -1;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return (intptr_t)done;
}

intptr_t camp_io_file_write(intptr_t handle, const void *buffer, uintptr_t length, int *error)
{
    DWORD done = 0;
    if (length > (uintptr_t)UINT32_MAX)
        length = (uintptr_t)UINT32_MAX;
    if (!WriteFile((HANDLE)handle, buffer, (DWORD)length, &done, NULL))
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        return -1;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return (intptr_t)done;
}

int64_t camp_io_file_seek(intptr_t handle, int64_t offset, int origin, int *error)
{
    LARGE_INTEGER distance;
    LARGE_INTEGER position;
    DWORD method;
    distance.QuadPart = offset;
    if (origin == CAMP_IO_SEEK_BEGIN)
        method = FILE_BEGIN;
    else if (origin == CAMP_IO_SEEK_CURRENT)
        method = FILE_CURRENT;
    else if (origin == CAMP_IO_SEEK_END)
        method = FILE_END;
    else
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return -1;
    }
    if (!SetFilePointerEx((HANDLE)handle, distance, &position, method))
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        return -1;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return (int64_t)position.QuadPart;
}

int camp_io_file_flush(intptr_t handle, int *error)
{
    if (!FlushFileBuffers((HANDLE)handle))
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        return 0;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

intptr_t camp_io_get_current_directory(char *buffer, uintptr_t length)
{
    DWORD required = GetCurrentDirectoryW(0, NULL);
    wchar_t *wide;
    intptr_t result;

    if (required == 0)
        return 0;

    wide = (wchar_t *)malloc((size_t)required * sizeof(wchar_t));
    if (wide == NULL)
        return 0;

    if (GetCurrentDirectoryW(required, wide) == 0)
    {
        free(wide);
        return 0;
    }

    result = camp_io_wide_to_utf8_buffer(wide, buffer, length);
    free(wide);
    return result;
}

int camp_io_set_current_directory(const char *path, int *error)
{
    wchar_t *wide = camp_io_utf8_to_wide(path, error);
    if (wide == NULL)
        return 0;
    if (!SetCurrentDirectoryW(wide))
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        free(wide);
        return 0;
    }
    free(wide);
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

intptr_t camp_io_get_executable_path(char *buffer, uintptr_t length)
{
    DWORD capacity = MAX_PATH;
    wchar_t *wide = NULL;
    DWORD written;
    intptr_t result;

    for (;;)
    {
        wchar_t *resized = (wchar_t *)realloc(wide, (size_t)capacity * sizeof(wchar_t));
        if (resized == NULL)
        {
            free(wide);
            return 0;
        }
        wide = resized;
        written = GetModuleFileNameW(NULL, wide, capacity);
        if (written == 0)
        {
            free(wide);
            return 0;
        }
        if (written < capacity - 1)
            break;
        if (capacity > (DWORD)(UINT_MAX / 2))
        {
            free(wide);
            return 0;
        }
        capacity *= 2;
    }

    result = camp_io_wide_to_utf8_buffer(wide, buffer, length);
    free(wide);
    return result;
}

intptr_t camp_io_get_environment_variable(const char *name, char *buffer, uintptr_t length)
{
    int error = CAMP_IO_OK;
    wchar_t *wide_name = camp_io_utf8_to_wide(name, &error);
    wchar_t *wide_value;
    DWORD required;
    intptr_t result;

    if (wide_name == NULL || !camp_io_valid_env_name_wide(wide_name))
    {
        free(wide_name);
        return 0;
    }

    SetLastError(ERROR_SUCCESS);
    required = GetEnvironmentVariableW(wide_name, NULL, 0);
    if (required == 0)
    {
        free(wide_name);
        return 0;
    }

    wide_value = (wchar_t *)malloc((size_t)required * sizeof(wchar_t));
    if (wide_value == NULL)
    {
        free(wide_name);
        return 0;
    }
    if (GetEnvironmentVariableW(wide_name, wide_value, required) == 0)
    {
        free(wide_value);
        free(wide_name);
        return 0;
    }

    result = camp_io_wide_to_utf8_buffer(wide_value, buffer, length);
    free(wide_value);
    free(wide_name);
    return result;
}

int camp_io_set_environment_variable(const char *name, const char *value, int *error)
{
    wchar_t *wide_name = camp_io_utf8_to_wide(name, error);
    wchar_t *wide_value = NULL;
    if (wide_name == NULL || !camp_io_valid_env_name_wide(wide_name))
    {
        free(wide_name);
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return 0;
    }
    wide_value = camp_io_utf8_to_wide(value, error);
    if (wide_value == NULL)
    {
        free(wide_name);
        return 0;
    }
    if (!SetEnvironmentVariableW(wide_name, wide_value))
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        free(wide_value);
        free(wide_name);
        return 0;
    }
    free(wide_value);
    free(wide_name);
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

int camp_io_remove_environment_variable(const char *name, int *error)
{
    wchar_t *wide_name = camp_io_utf8_to_wide(name, error);
    int existed;
    if (wide_name == NULL || !camp_io_valid_env_name_wide(wide_name))
    {
        free(wide_name);
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return -1;
    }
    existed = GetEnvironmentVariableW(wide_name, NULL, 0) != 0 || GetLastError() != ERROR_ENVVAR_NOT_FOUND;
    if (!SetEnvironmentVariableW(wide_name, NULL))
    {
        DWORD last = GetLastError();
        free(wide_name);
        if (last == ERROR_ENVVAR_NOT_FOUND)
        {
            camp_io_set_error(error, CAMP_IO_OK);
            return 0;
        }
        camp_io_set_error(error, camp_io_error_from_windows(last));
        return -1;
    }
    free(wide_name);
    camp_io_set_error(error, CAMP_IO_OK);
    return existed ? 1 : 0;
}

int camp_io_has_environment_variable(const char *name)
{
    int error = CAMP_IO_OK;
    wchar_t *wide_name = camp_io_utf8_to_wide(name, &error);
    int result;
    if (wide_name == NULL || !camp_io_valid_env_name_wide(wide_name))
    {
        free(wide_name);
        return 0;
    }
    SetLastError(ERROR_SUCCESS);
    result = GetEnvironmentVariableW(wide_name, NULL, 0) != 0 || GetLastError() != ERROR_ENVVAR_NOT_FOUND;
    free(wide_name);
    return result ? 1 : 0;
}

int camp_io_create_directory(const char *path, int *error)
{
    wchar_t *wide = camp_io_utf8_to_wide(path, error);
    DWORD attributes;
    if (wide == NULL)
        return 0;
    if (CreateDirectoryW(wide, NULL))
    {
        free(wide);
        camp_io_set_error(error, CAMP_IO_OK);
        return 1;
    }
    attributes = GetFileAttributesW(wide);
    if (attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
    {
        free(wide);
        camp_io_set_error(error, CAMP_IO_OK);
        return 1;
    }
    camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
    free(wide);
    return 0;
}

int camp_io_delete_directory(const char *path, int *error)
{
    wchar_t *wide = camp_io_utf8_to_wide(path, error);
    if (wide == NULL)
        return 0;
    if (!RemoveDirectoryW(wide))
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        free(wide);
        return 0;
    }
    free(wide);
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

int camp_io_delete_file(const char *path, int *error)
{
    wchar_t *wide = camp_io_utf8_to_wide(path, error);
    if (wide == NULL)
        return 0;
    if (!DeleteFileW(wide))
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        free(wide);
        return 0;
    }
    free(wide);
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

int camp_io_copy_file(const char *source, const char *dest, int overwrite, int *error)
{
    wchar_t *wide_source = camp_io_utf8_to_wide(source, error);
    wchar_t *wide_dest = camp_io_utf8_to_wide(dest, error);
    if (wide_source == NULL || wide_dest == NULL)
    {
        free(wide_source);
        free(wide_dest);
        return 0;
    }
    if (!CopyFileW(wide_source, wide_dest, overwrite ? FALSE : TRUE))
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        free(wide_dest);
        free(wide_source);
        return 0;
    }
    free(wide_dest);
    free(wide_source);
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

int camp_io_move_file(const char *source, const char *dest, int overwrite, int *error)
{
    wchar_t *wide_source = camp_io_utf8_to_wide(source, error);
    wchar_t *wide_dest = camp_io_utf8_to_wide(dest, error);
    DWORD flags = overwrite ? MOVEFILE_REPLACE_EXISTING : 0;
    if (wide_source == NULL || wide_dest == NULL)
    {
        free(wide_source);
        free(wide_dest);
        return 0;
    }
    if (!MoveFileExW(wide_source, wide_dest, flags))
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        free(wide_dest);
        free(wide_source);
        return 0;
    }
    free(wide_dest);
    free(wide_source);
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

int camp_io_path_exists(const char *path)
{
    int error = CAMP_IO_OK;
    wchar_t *wide = camp_io_utf8_to_wide(path, &error);
    DWORD attributes;
    if (wide == NULL)
        return 0;
    attributes = GetFileAttributesW(wide);
    free(wide);
    return attributes != INVALID_FILE_ATTRIBUTES ? 1 : 0;
}

int camp_io_path_is_directory(const char *path)
{
    int error = CAMP_IO_OK;
    wchar_t *wide = camp_io_utf8_to_wide(path, &error);
    DWORD attributes;
    if (wide == NULL)
        return 0;
    attributes = GetFileAttributesW(wide);
    free(wide);
    return attributes != INVALID_FILE_ATTRIBUTES && (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0 ? 1 : 0;
}

int camp_io_file_get_size(const char *path, uint64_t *size, int *error)
{
    wchar_t *wide = camp_io_utf8_to_wide(path, error);
    WIN32_FILE_ATTRIBUTE_DATA data;
    ULARGE_INTEGER combined;
    if (wide == NULL)
        return 0;
    if (!GetFileAttributesExW(wide, GetFileExInfoStandard, &data))
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        free(wide);
        return 0;
    }
    if ((data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
    {
        camp_io_set_error(error, CAMP_IO_IS_DIRECTORY);
        free(wide);
        return 0;
    }
    combined.HighPart = data.nFileSizeHigh;
    combined.LowPart = data.nFileSizeLow;
    if (size != NULL)
        *size = (uint64_t)combined.QuadPart;
    free(wide);
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

static int camp_io_create_directory_tree_wide(wchar_t *path, int *error)
{
    wchar_t *current;
    DWORD attributes;

    if (path == NULL || path[0] == L'\0')
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return 0;
    }

    for (current = path; *current != L'\0'; current++)
    {
        if (*current != L'\\' && *current != L'/')
            continue;
        if (current == path)
            continue;
        if (current == path + 2 && path[1] == L':')
            continue;

        *current = L'\0';
        if (path[0] != L'\0')
        {
            attributes = GetFileAttributesW(path);
            if (attributes == INVALID_FILE_ATTRIBUTES)
            {
                if (!CreateDirectoryW(path, NULL))
                {
                    camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
                    *current = L'\\';
                    return 0;
                }
            }
            else if ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                camp_io_set_error(error, CAMP_IO_NOT_DIRECTORY);
                *current = L'\\';
                return 0;
            }
        }
        *current = L'\\';
    }

    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

int camp_io_create_directory_recursive(const char *path, int *error)
{
    wchar_t *wide = camp_io_utf8_to_wide(path, error);
    DWORD attributes;
    if (wide == NULL)
        return 0;

    if (!camp_io_create_directory_tree_wide(wide, error))
    {
        free(wide);
        return 0;
    }

    attributes = GetFileAttributesW(wide);
    if (attributes == INVALID_FILE_ATTRIBUTES)
    {
        if (!CreateDirectoryW(wide, NULL))
        {
            camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
            free(wide);
            return 0;
        }
    }
    else if ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
    {
        camp_io_set_error(error, CAMP_IO_NOT_DIRECTORY);
        free(wide);
        return 0;
    }

    free(wide);
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

static int camp_io_delete_directory_tree_wide(const wchar_t *path, int *error)
{
    size_t base_length = wcslen(path);
    size_t pattern_length = base_length;
    wchar_t *pattern = (wchar_t *)malloc((base_length + 3) * sizeof(wchar_t));
    WIN32_FIND_DATAW entry;
    HANDLE find;
    int ok = 1;

    if (pattern == NULL)
    {
        camp_io_set_error(error, CAMP_IO_NO_MEMORY);
        return 0;
    }
    wcscpy(pattern, path);
    if (pattern_length > 0 && pattern[pattern_length - 1] != L'\\' && pattern[pattern_length - 1] != L'/')
        pattern[pattern_length++] = L'\\';
    pattern[pattern_length++] = L'*';
    pattern[pattern_length] = L'\0';

    find = FindFirstFileW(pattern, &entry);
    free(pattern);
    if (find == INVALID_HANDLE_VALUE)
    {
        DWORD last = GetLastError();
        if (last != ERROR_FILE_NOT_FOUND && last != ERROR_PATH_NOT_FOUND)
        {
            camp_io_set_error(error, camp_io_error_from_windows(last));
            return 0;
        }
    }
    else
    {
        do
        {
            size_t name_length;
            wchar_t *child;
            if (wcscmp(entry.cFileName, L".") == 0 || wcscmp(entry.cFileName, L"..") == 0)
                continue;

            name_length = wcslen(entry.cFileName);
            child = (wchar_t *)malloc((base_length + name_length + 2) * sizeof(wchar_t));
            if (child == NULL)
            {
                camp_io_set_error(error, CAMP_IO_NO_MEMORY);
                ok = 0;
                break;
            }
            wcscpy(child, path);
            if (base_length > 0 && child[base_length - 1] != L'\\' && child[base_length - 1] != L'/')
            {
                child[base_length] = L'\\';
                child[base_length + 1] = L'\0';
            }
            wcscat(child, entry.cFileName);

            if ((entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0
                && (entry.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == 0)
            {
                ok = camp_io_delete_directory_tree_wide(child, error);
            }
            else if ((entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                if (!RemoveDirectoryW(child))
                {
                    camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
                    ok = 0;
                }
            }
            else if (!DeleteFileW(child))
            {
                camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
                ok = 0;
            }
            free(child);
        } while (ok && FindNextFileW(find, &entry));

        if (ok && GetLastError() != ERROR_NO_MORE_FILES)
        {
            camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
            ok = 0;
        }
        FindClose(find);
        if (!ok)
            return 0;
    }

    if (!RemoveDirectoryW(path))
    {
        camp_io_set_error(error, camp_io_error_from_windows(GetLastError()));
        return 0;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

int camp_io_delete_directory_recursive(const char *path, int *error)
{
    wchar_t *wide = camp_io_utf8_to_wide(path, error);
    int result;
    if (wide == NULL)
        return 0;
    result = camp_io_delete_directory_tree_wide(wide, error);
    free(wide);
    return result;
}

#else

static int camp_io_error_from_errno(int error)
{
    switch (error)
    {
        case 0:
            return CAMP_IO_OK;
        case ENOENT:
            return CAMP_IO_NOT_FOUND;
        case EEXIST:
            return CAMP_IO_ALREADY_EXISTS;
        case EACCES:
        case EPERM:
            return CAMP_IO_PERMISSION_DENIED;
#ifdef EBUSY
        case EBUSY:
            return CAMP_IO_BUSY;
#endif
        case EINTR:
            return CAMP_IO_INTERRUPTED;
#ifdef EAGAIN
        case EAGAIN:
            return CAMP_IO_WOULD_BLOCK;
#endif
#if defined(EWOULDBLOCK) && (!defined(EAGAIN) || EWOULDBLOCK != EAGAIN)
        case EWOULDBLOCK:
            return CAMP_IO_WOULD_BLOCK;
#endif
        case EMFILE:
        case ENFILE:
            return CAMP_IO_TOO_MANY_OPEN_FILES;
#ifdef ENAMETOOLONG
        case ENAMETOOLONG:
            return CAMP_IO_PATH_TOO_LONG;
#endif
        case ENOSPC:
            return CAMP_IO_NO_SPACE;
#ifdef EROFS
        case EROFS:
            return CAMP_IO_READ_ONLY;
#endif
#ifdef EISDIR
        case EISDIR:
            return CAMP_IO_IS_DIRECTORY;
#endif
        case ENOTDIR:
            return CAMP_IO_NOT_DIRECTORY;
#ifdef ENOTEMPTY
        case ENOTEMPTY:
            return CAMP_IO_DIRECTORY_NOT_EMPTY;
#endif
#ifdef EPIPE
        case EPIPE:
            return CAMP_IO_BROKEN_PIPE;
#endif
        case ENOMEM:
            return CAMP_IO_NO_MEMORY;
        case EINVAL:
        case EBADF:
            return CAMP_IO_INVALID_ARGUMENT;
#ifdef ETIMEDOUT
        case ETIMEDOUT:
            return CAMP_IO_TIMEOUT;
#endif
        case EIO:
            return CAMP_IO_IO;
        default:
            return CAMP_IO_UNKNOWN;
    }
}

static int camp_io_valid_env_name(const char *name)
{
    const char *current;
    if (name == NULL || name[0] == '\0')
        return 0;
    for (current = name; *current != '\0'; current++)
        if (*current == '=')
            return 0;
    return 1;
}

intptr_t camp_io_stdin(void)
{
    return 0;
}

intptr_t camp_io_stdout(void)
{
    return 1;
}

intptr_t camp_io_stderr(void)
{
    return 2;
}

intptr_t camp_io_file_open(const char *path, int access, int mode, int options, int *error)
{
    int flags = 0;
    int fd;
    (void)options;

    if (access == CAMP_IO_ACCESS_READ)
        flags = O_RDONLY;
    else if (access == CAMP_IO_ACCESS_WRITE)
        flags = O_WRONLY;
    else if (access == CAMP_IO_ACCESS_READ_WRITE)
        flags = O_RDWR;
    else
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return CAMP_IO_INVALID_HANDLE;
    }

    if (mode == CAMP_IO_MODE_CREATE)
        flags |= O_CREAT | O_EXCL;
    else if (mode == CAMP_IO_MODE_CREATE_OR_TRUNCATE)
        flags |= O_CREAT | O_TRUNC;
    else if (mode == CAMP_IO_MODE_APPEND)
        flags |= O_CREAT | O_APPEND;
    else if (mode != CAMP_IO_MODE_OPEN_EXISTING)
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return CAMP_IO_INVALID_HANDLE;
    }

    if (path == NULL)
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return CAMP_IO_INVALID_HANDLE;
    }

    fd = open(path, flags, 0666);
    if (fd < 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return CAMP_IO_INVALID_HANDLE;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return (intptr_t)fd;
}

int camp_io_file_close(intptr_t handle, int *error)
{
    if (close((int)handle) < 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return 0;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

intptr_t camp_io_file_read(intptr_t handle, void *buffer, uintptr_t length, int *error)
{
    ssize_t result;
    result = read((int)handle, buffer, (size_t)length);
    if (result < 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return -1;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return (intptr_t)result;
}

intptr_t camp_io_file_write(intptr_t handle, const void *buffer, uintptr_t length, int *error)
{
    ssize_t result;
    result = write((int)handle, buffer, (size_t)length);
    if (result < 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return -1;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return (intptr_t)result;
}

int64_t camp_io_file_seek(intptr_t handle, int64_t offset, int origin, int *error)
{
    int whence;
    off_t result;
    if (origin == CAMP_IO_SEEK_BEGIN)
        whence = SEEK_SET;
    else if (origin == CAMP_IO_SEEK_CURRENT)
        whence = SEEK_CUR;
    else if (origin == CAMP_IO_SEEK_END)
        whence = SEEK_END;
    else
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return -1;
    }

    result = lseek((int)handle, (off_t)offset, whence);
    if (result < 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return -1;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return (int64_t)result;
}

int camp_io_file_flush(intptr_t handle, int *error)
{
    if (fsync((int)handle) < 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return 0;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

intptr_t camp_io_get_current_directory(char *buffer, uintptr_t length)
{
    char *cwd = getcwd(NULL, 0);
    intptr_t result;
    if (cwd == NULL)
        return 0;
    result = camp_io_copy_utf8_result(cwd, (uintptr_t)strlen(cwd), buffer, length);
    free(cwd);
    return result;
}

int camp_io_set_current_directory(const char *path, int *error)
{
    if (path == NULL)
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return 0;
    }
    if (chdir(path) != 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return 0;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

intptr_t camp_io_get_executable_path(char *buffer, uintptr_t length)
{
#if defined(__linux__)
    char stack_buffer[4096];
    char *path = stack_buffer;
    size_t capacity = sizeof(stack_buffer);
    ssize_t written;
    for (;;)
    {
        written = readlink("/proc/self/exe", path, capacity - 1);
        if (written < 0)
        {
            if (path != stack_buffer)
                free(path);
            return 0;
        }
        if ((size_t)written < capacity - 1)
            break;
        if (path != stack_buffer)
            free(path);
        capacity *= 2;
        path = (char *)malloc(capacity);
        if (path == NULL)
            return 0;
    }
    path[written] = '\0';
    {
        intptr_t result = camp_io_copy_utf8_result(path, (uintptr_t)written, buffer, length);
        if (path != stack_buffer)
            free(path);
        return result;
    }
#elif defined(__APPLE__)
    uint32_t size = 0;
    char *path;
    intptr_t result;
    if (_NSGetExecutablePath(NULL, &size) != -1 || size == 0)
        return 0;
    path = (char *)malloc((size_t)size);
    if (path == NULL)
        return 0;
    if (_NSGetExecutablePath(path, &size) != 0)
    {
        free(path);
        return 0;
    }
    result = camp_io_copy_utf8_result(path, (uintptr_t)strlen(path), buffer, length);
    free(path);
    return result;
#else
    (void)buffer;
    (void)length;
    return 0;
#endif
}

intptr_t camp_io_get_environment_variable(const char *name, char *buffer, uintptr_t length)
{
    const char *value;
    if (!camp_io_valid_env_name(name))
        return 0;
    value = getenv(name);
    if (value == NULL)
        return 0;
    return camp_io_copy_utf8_result(value, (uintptr_t)strlen(value), buffer, length);
}

int camp_io_set_environment_variable(const char *name, const char *value, int *error)
{
    if (!camp_io_valid_env_name(name) || value == NULL)
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return 0;
    }
#if defined(__EMSCRIPTEN__) || defined(__wasi__)
    camp_io_set_error(error, CAMP_IO_NOT_SUPPORTED);
    return 0;
#else
    if (setenv(name, value, 1) != 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return 0;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
#endif
}

int camp_io_remove_environment_variable(const char *name, int *error)
{
    int existed;
    if (!camp_io_valid_env_name(name))
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return -1;
    }
#if defined(__EMSCRIPTEN__) || defined(__wasi__)
    camp_io_set_error(error, CAMP_IO_NOT_SUPPORTED);
    return -1;
#else
    existed = getenv(name) != NULL;
    if (unsetenv(name) != 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return -1;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return existed ? 1 : 0;
#endif
}

int camp_io_has_environment_variable(const char *name)
{
    if (!camp_io_valid_env_name(name))
        return 0;
    return getenv(name) != NULL ? 1 : 0;
}

int camp_io_create_directory(const char *path, int *error)
{
    struct stat info;
    if (path == NULL)
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return 0;
    }
    if (mkdir(path, 0777) == 0)
    {
        camp_io_set_error(error, CAMP_IO_OK);
        return 1;
    }
    if (errno == EEXIST && stat(path, &info) == 0 && S_ISDIR(info.st_mode))
    {
        camp_io_set_error(error, CAMP_IO_OK);
        return 1;
    }
    camp_io_set_error(error, camp_io_error_from_errno(errno));
    return 0;
}

int camp_io_delete_directory(const char *path, int *error)
{
    if (path == NULL)
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return 0;
    }
    if (rmdir(path) != 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return 0;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

int camp_io_delete_file(const char *path, int *error)
{
    if (path == NULL)
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return 0;
    }
    if (unlink(path) != 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return 0;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

int camp_io_copy_file(const char *source, const char *dest, int overwrite, int *error)
{
    int input = -1;
    int output = -1;
    int flags = O_WRONLY | O_CREAT;
    char buffer[32768];
    ssize_t read_count;
    struct stat info;

    if (source == NULL || dest == NULL)
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return 0;
    }
    input = open(source, O_RDONLY);
    if (input < 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return 0;
    }
    if (fstat(input, &info) != 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        close(input);
        return 0;
    }
    if (S_ISDIR(info.st_mode))
    {
        camp_io_set_error(error, CAMP_IO_IS_DIRECTORY);
        close(input);
        return 0;
    }

    flags |= overwrite ? O_TRUNC : O_EXCL;
    output = open(dest, flags, 0666);
    if (output < 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        close(input);
        return 0;
    }

    while ((read_count = read(input, buffer, sizeof(buffer))) > 0)
    {
        ssize_t offset = 0;
        while (offset < read_count)
        {
            ssize_t written = write(output, buffer + offset, (size_t)(read_count - offset));
            if (written <= 0)
            {
                camp_io_set_error(error, written < 0 ? camp_io_error_from_errno(errno) : CAMP_IO_IO);
                close(output);
                close(input);
                return 0;
            }
            offset += written;
        }
    }
    if (read_count < 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        close(output);
        close(input);
        return 0;
    }
    if (close(output) != 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        close(input);
        return 0;
    }
    close(input);
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

int camp_io_move_file(const char *source, const char *dest, int overwrite, int *error)
{
    if (source == NULL || dest == NULL)
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return 0;
    }
    if (!overwrite)
    {
        struct stat existing;
        if (lstat(dest, &existing) == 0)
        {
            camp_io_set_error(error, CAMP_IO_ALREADY_EXISTS);
            return 0;
        }
    }
    if (rename(source, dest) != 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return 0;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

int camp_io_path_exists(const char *path)
{
    struct stat info;
    if (path == NULL)
        return 0;
    return stat(path, &info) == 0 ? 1 : 0;
}

int camp_io_path_is_directory(const char *path)
{
    struct stat info;
    if (path == NULL)
        return 0;
    return stat(path, &info) == 0 && S_ISDIR(info.st_mode) ? 1 : 0;
}

int camp_io_file_get_size(const char *path, uint64_t *size, int *error)
{
    struct stat info;
    if (path == NULL)
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return 0;
    }
    if (stat(path, &info) != 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return 0;
    }
    if (S_ISDIR(info.st_mode))
    {
        camp_io_set_error(error, CAMP_IO_IS_DIRECTORY);
        return 0;
    }
    if (info.st_size < 0)
    {
        camp_io_set_error(error, CAMP_IO_IO);
        return 0;
    }
    if (size != NULL)
        *size = (uint64_t)info.st_size;
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

int camp_io_create_directory_recursive(const char *path, int *error)
{
    char *copy;
    char *current;
    struct stat info;

    if (path == NULL || path[0] == '\0')
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return 0;
    }

    copy = (char *)malloc(strlen(path) + 1);
    if (copy == NULL)
    {
        camp_io_set_error(error, CAMP_IO_NO_MEMORY);
        return 0;
    }
    strcpy(copy, path);

    for (current = copy + 1; *current != '\0'; current++)
    {
        if (*current != '/')
            continue;
        *current = '\0';
        if (copy[0] != '\0')
        {
            if (mkdir(copy, 0777) != 0 && errno != EEXIST)
            {
                camp_io_set_error(error, camp_io_error_from_errno(errno));
                free(copy);
                return 0;
            }
            if (stat(copy, &info) != 0 || !S_ISDIR(info.st_mode))
            {
                camp_io_set_error(error, CAMP_IO_NOT_DIRECTORY);
                free(copy);
                return 0;
            }
        }
        *current = '/';
    }

    free(copy);
    return camp_io_create_directory(path, error);
}

static int camp_io_delete_directory_tree(const char *path, int *error)
{
    DIR *dir = opendir(path);
    struct dirent *entry;

    if (dir == NULL)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return 0;
    }

    while ((entry = readdir(dir)) != NULL)
    {
        size_t path_length;
        size_t name_length;
        char *child;
        struct stat info;

        if (strcmp(entry->d_name, ".") == 0 || strcmp(entry->d_name, "..") == 0)
            continue;

        path_length = strlen(path);
        name_length = strlen(entry->d_name);
        child = (char *)malloc(path_length + name_length + 2);
        if (child == NULL)
        {
            closedir(dir);
            camp_io_set_error(error, CAMP_IO_NO_MEMORY);
            return 0;
        }
        strcpy(child, path);
        if (path_length > 0 && child[path_length - 1] != '/')
        {
            child[path_length] = '/';
            child[path_length + 1] = '\0';
        }
        strcat(child, entry->d_name);

        if (lstat(child, &info) != 0)
        {
            camp_io_set_error(error, camp_io_error_from_errno(errno));
            free(child);
            closedir(dir);
            return 0;
        }

        if (S_ISDIR(info.st_mode))
        {
            if (!camp_io_delete_directory_tree(child, error))
            {
                free(child);
                closedir(dir);
                return 0;
            }
        }
        else if (unlink(child) != 0)
        {
            camp_io_set_error(error, camp_io_error_from_errno(errno));
            free(child);
            closedir(dir);
            return 0;
        }
        free(child);
    }

    if (closedir(dir) != 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return 0;
    }
    if (rmdir(path) != 0)
    {
        camp_io_set_error(error, camp_io_error_from_errno(errno));
        return 0;
    }
    camp_io_set_error(error, CAMP_IO_OK);
    return 1;
}

int camp_io_delete_directory_recursive(const char *path, int *error)
{
    if (path == NULL)
    {
        camp_io_set_error(error, CAMP_IO_INVALID_ARGUMENT);
        return 0;
    }
    return camp_io_delete_directory_tree(path, error);
}

#endif
