#include <stdint.h>
#include <limits.h>
#include <stdlib.h>
#include <string.h>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#else
#include <errno.h>
#include <fcntl.h>
#include <unistd.h>
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

#endif
