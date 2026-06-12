/*
   Camp Std::Time platform abstraction layer.
   Functions return true on success and false when the service is unavailable.

   Define CAMP_TIME_FORCE_FALLBACK to avoid platform-specific APIs.
   Define CAMP_TIME_NO_CLOCK_GETTIME to avoid POSIX clock_gettime().
   Define CAMP_TIME_PAL_API before including/compiling to add export attributes.
*/

#if !defined(_WIN32) && !defined(CAMP_TIME_FORCE_FALLBACK) && \
    (defined(CAMP_TIME_FORCE_POSIX) || defined(__unix__) || defined(__unix) || \
     defined(__APPLE__) || defined(__MACH__) || defined(__linux__) || defined(__CYGWIN__))
#define CAMP_TIME_POSIX 1
#ifndef _POSIX_C_SOURCE
#define _POSIX_C_SOURCE 200809L
#endif
#endif

#if defined(_WIN32) && !defined(CAMP_TIME_FORCE_FALLBACK)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#endif

#if defined(__STDC_VERSION__) && (__STDC_VERSION__ >= 199901L)
#include <stdint.h>
#include <stdbool.h>
#else
#if defined(_MSC_VER)
typedef signed __int64 int64_t;
typedef unsigned __int64 uint64_t;
#else
typedef signed long long int64_t;
typedef unsigned long long uint64_t;
#endif
typedef unsigned short uint16_t;
typedef signed short int16_t;
typedef unsigned char uint8_t;
typedef int bool;
#ifndef true
#define true 1
#endif
#ifndef false
#define false 0
#endif
#endif

#include <time.h>

#ifndef CAMP_TIME_PAL_API
#define CAMP_TIME_PAL_API
#endif

static uint8_t camp_time_ms_to_fraction(unsigned int ms)
{
    if (ms >= 1000U)
        return (uint8_t)255U;

    return (uint8_t)((ms * 256U) / 1000U);
}

static int camp_time_digit(char c)
{
    if (c < '0' || c > '9')
        return -1;

    return (int)(c - '0');
}

static bool camp_time_set_offset_minutes(int minutes, int16_t *out_minutes)
{
    if (out_minutes == 0)
        return false;

    if (minutes < -1080 || minutes > 1080)
        return false;

    *out_minutes = (int16_t)minutes;
    return true;
}

static bool camp_time_parse_offset_text(const char *text, int16_t *out_minutes)
{
    int sign;
    int h1;
    int h2;
    int m1;
    int m2;
    int pos;
    int total;

    if (text == 0 || out_minutes == 0 || text[0] == '\0')
        return false;

    if ((text[0] == 'Z' || text[0] == 'z') && text[1] == '\0')
        return camp_time_set_offset_minutes(0, out_minutes);

    if (text[0] == '+')
        sign = 1;
    else if (text[0] == '-')
        sign = -1;
    else
        return false;

    h1 = camp_time_digit(text[1]);
    h2 = camp_time_digit(text[2]);

    if (h1 < 0 || h2 < 0)
        return false;

    pos = 3;
    m1 = 0;
    m2 = 0;

    if (text[pos] != '\0')
    {
        if (text[pos] == ':')
            pos++;

        m1 = camp_time_digit(text[pos]);
        m2 = camp_time_digit(text[pos + 1]);

        if (m1 < 0 || m2 < 0)
            return false;

        pos += 2;
    }

    if (text[pos] != '\0')
        return false;

    total = ((h1 * 10 + h2) * 60) + (m1 * 10 + m2);
    return camp_time_set_offset_minutes(sign * total, out_minutes);
}

static bool camp_time_fill_from_tm(const struct tm *value,
                                   unsigned int ms,
                                   uint16_t *year,
                                   uint8_t *month,
                                   uint8_t *day,
                                   uint8_t *hour,
                                   uint8_t *minute,
                                   uint8_t *second,
                                   uint8_t *fraction)
{
    int full_year;

    if (value == 0 || year == 0 || month == 0 || day == 0 ||
        hour == 0 || minute == 0 || second == 0 || fraction == 0)
        return false;

    full_year = value->tm_year + 1900;

    if (full_year < 0 || full_year > 65535)
        return false;

    if (value->tm_mon < 0 || value->tm_mon > 11 ||
        value->tm_mday < 1 || value->tm_mday > 31 ||
        value->tm_hour < 0 || value->tm_hour > 23 ||
        value->tm_min < 0 || value->tm_min > 59 ||
        value->tm_sec < 0 || value->tm_sec > 60)
        return false;

    *year = (uint16_t)full_year;
    *month = (uint8_t)(value->tm_mon + 1);
    *day = (uint8_t)value->tm_mday;
    *hour = (uint8_t)value->tm_hour;
    *minute = (uint8_t)value->tm_min;
    *second = (uint8_t)(value->tm_sec > 59 ? 59 : value->tm_sec);
    *fraction = camp_time_ms_to_fraction(ms);
    return true;
}

static bool camp_time_time_to_ms(time_t seconds, long ms, int64_t *out_ms)
{
    if (out_ms == 0)
        return false;

    if (ms < 0)
        ms = 0;
    else if (ms > 999)
        ms = 999;

    *out_ms = ((int64_t)seconds * (int64_t)1000) + (int64_t)ms;
    return true;
}

#if defined(_WIN32) && !defined(CAMP_TIME_FORCE_FALLBACK)

CAMP_TIME_PAL_API bool camp_time_get_utc_now_ms(int64_t *unix_ms)
{
    FILETIME ft;
    uint64_t raw;
    uint64_t unix_100ns;
    const uint64_t epoch_delta_100ns = (uint64_t)116444736000000000ULL;

    if (unix_ms == 0)
        return false;

    GetSystemTimeAsFileTime(&ft);

    raw = (((uint64_t)ft.dwHighDateTime) << 32) | (uint64_t)ft.dwLowDateTime;

    if (raw < epoch_delta_100ns)
        return false;

    unix_100ns = raw - epoch_delta_100ns;
    *unix_ms = (int64_t)(unix_100ns / (uint64_t)10000);
    return true;
}

CAMP_TIME_PAL_API bool camp_time_get_local_now(uint16_t *year,
                                               uint8_t *month,
                                               uint8_t *day,
                                               uint8_t *hour,
                                               uint8_t *minute,
                                               uint8_t *second,
                                               uint8_t *fraction)
{
    SYSTEMTIME st;

    if (year == 0 || month == 0 || day == 0 || hour == 0 ||
        minute == 0 || second == 0 || fraction == 0)
        return false;

    GetLocalTime(&st);

    *year = (uint16_t)st.wYear;
    *month = (uint8_t)st.wMonth;
    *day = (uint8_t)st.wDay;
    *hour = (uint8_t)st.wHour;
    *minute = (uint8_t)st.wMinute;
    *second = (uint8_t)st.wSecond;
    *fraction = camp_time_ms_to_fraction((unsigned int)st.wMilliseconds);
    return true;
}

CAMP_TIME_PAL_API bool camp_time_get_local_offset_minutes(int16_t *minutes_east)
{
    TIME_ZONE_INFORMATION tzi;
    DWORD id;
    LONG bias;

    if (minutes_east == 0)
        return false;

    id = GetTimeZoneInformation(&tzi);

    if (id == TIME_ZONE_ID_INVALID)
        return false;

    bias = tzi.Bias;

    if (id == TIME_ZONE_ID_DAYLIGHT)
        bias += tzi.DaylightBias;
    else if (id == TIME_ZONE_ID_STANDARD)
        bias += tzi.StandardBias;

    /* Windows bias is minutes to add to local time to get UTC. */
    return camp_time_set_offset_minutes((int)(-bias), minutes_east);
}

#elif defined(CAMP_TIME_POSIX)

static bool camp_time_posix_now(time_t *seconds, long *ms)
{
#if !defined(CAMP_TIME_NO_CLOCK_GETTIME) && defined(CLOCK_REALTIME)
    struct timespec ts;

    if (clock_gettime(CLOCK_REALTIME, &ts) == 0)
    {
        if (seconds != 0)
            *seconds = ts.tv_sec;
        if (ms != 0)
            *ms = ts.tv_nsec / 1000000L;
        return true;
    }
#endif

    if (seconds != 0)
    {
        time_t t;

        t = time(0);

        if (t == (time_t)-1)
            return false;

        *seconds = t;
    }

    if (ms != 0)
        *ms = 0;

    return true;
}

CAMP_TIME_PAL_API bool camp_time_get_utc_now_ms(int64_t *unix_ms)
{
    time_t seconds;
    long ms;

    if (!camp_time_posix_now(&seconds, &ms))
        return false;

    return camp_time_time_to_ms(seconds, ms, unix_ms);
}

CAMP_TIME_PAL_API bool camp_time_get_local_now(uint16_t *year,
                                               uint8_t *month,
                                               uint8_t *day,
                                               uint8_t *hour,
                                               uint8_t *minute,
                                               uint8_t *second,
                                               uint8_t *fraction)
{
    time_t seconds;
    long ms;
    struct tm local_value;

    if (!camp_time_posix_now(&seconds, &ms))
        return false;

    if (localtime_r(&seconds, &local_value) == 0)
        return false;

    return camp_time_fill_from_tm(&local_value,
                                  (unsigned int)ms,
                                  year,
                                  month,
                                  day,
                                  hour,
                                  minute,
                                  second,
                                  fraction);
}

CAMP_TIME_PAL_API bool camp_time_get_local_offset_minutes(int16_t *minutes_east)
{
    time_t seconds;
    long ms;
    struct tm local_value;
    char buffer[16];

    (void)ms;

    if (minutes_east == 0)
        return false;

    if (!camp_time_posix_now(&seconds, &ms))
        return false;

    if (localtime_r(&seconds, &local_value) == 0)
        return false;

    /* POSIX %z is the current numeric offset, including seasonal adjustment. */
    if (strftime(buffer, sizeof(buffer), "%z", &local_value) == 0)
        return false;

    return camp_time_parse_offset_text(buffer, minutes_east);
}

#else

static bool camp_time_fallback_localtime(time_t seconds, struct tm *out_value)
{
    struct tm *value;

    if (out_value == 0)
        return false;

    value = localtime(&seconds);

    if (value == 0)
        return false;

    *out_value = *value;
    return true;
}

static bool camp_time_fallback_gmtime(time_t seconds, struct tm *out_value)
{
    struct tm *value;

    if (out_value == 0)
        return false;

    value = gmtime(&seconds);

    if (value == 0)
        return false;

    *out_value = *value;
    return true;
}

CAMP_TIME_PAL_API bool camp_time_get_utc_now_ms(int64_t *unix_ms)
{
    time_t seconds;

    seconds = time(0);

    if (seconds == (time_t)-1)
        return false;

    return camp_time_time_to_ms(seconds, 0, unix_ms);
}

CAMP_TIME_PAL_API bool camp_time_get_local_now(uint16_t *year,
                                               uint8_t *month,
                                               uint8_t *day,
                                               uint8_t *hour,
                                               uint8_t *minute,
                                               uint8_t *second,
                                               uint8_t *fraction)
{
    time_t seconds;
    struct tm local_value;

    seconds = time(0);

    if (seconds == (time_t)-1)
        return false;

    if (!camp_time_fallback_localtime(seconds, &local_value))
        return false;

    return camp_time_fill_from_tm(&local_value,
                                  0,
                                  year,
                                  month,
                                  day,
                                  hour,
                                  minute,
                                  second,
                                  fraction);
}

CAMP_TIME_PAL_API bool camp_time_get_local_offset_minutes(int16_t *minutes_east)
{
    time_t seconds;
    struct tm local_value;
    struct tm gm_value;
    char buffer[16];
    time_t local_as_time;
    time_t gm_as_local_time;
    double diff_seconds;
    int minutes;

    if (minutes_east == 0)
        return false;

    seconds = time(0);

    if (seconds == (time_t)-1)
        return false;

    if (!camp_time_fallback_localtime(seconds, &local_value))
        return false;

    if (strftime(buffer, sizeof(buffer), "%z", &local_value) != 0 &&
        camp_time_parse_offset_text(buffer, minutes_east))
        return true;

    if (!camp_time_fallback_gmtime(seconds, &gm_value))
        return false;

    local_value.tm_isdst = -1;
    gm_value.tm_isdst = -1;

    local_as_time = mktime(&local_value);
    gm_as_local_time = mktime(&gm_value);

    if (local_as_time == (time_t)-1 || gm_as_local_time == (time_t)-1)
        return false;

    diff_seconds = difftime(local_as_time, gm_as_local_time);

    if (diff_seconds >= 0.0)
        minutes = (int)((diff_seconds / 60.0) + 0.5);
    else
        minutes = (int)((diff_seconds / 60.0) - 0.5);

    return camp_time_set_offset_minutes(minutes, minutes_east);
}

#endif
