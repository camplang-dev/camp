#include <stdint.h>

#if defined(_MSC_VER)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <intrin.h>
#endif

#if defined(_MSC_VER)

intptr_t atomicExchangeNInt(intptr_t *dest, intptr_t value)
{
    return (intptr_t)_InterlockedExchangePointer((void **)dest, (void *)value);
}

uintptr_t atomicExchangeNUInt(uintptr_t *dest, uintptr_t value)
{
    return (uintptr_t)_InterlockedExchangePointer((void **)dest, (void *)value);
}

void *atomicExchangePtr(void **dest, void *value)
{
    return _InterlockedExchangePointer(dest, value);
}

intptr_t atomicCompareExchangeNInt(intptr_t *dest, intptr_t expected, intptr_t value)
{
    return (intptr_t)_InterlockedCompareExchangePointer((void **)dest, (void *)value, (void *)expected);
}

uintptr_t atomicCompareExchangeNUInt(uintptr_t *dest, uintptr_t expected, uintptr_t value)
{
    return (uintptr_t)_InterlockedCompareExchangePointer((void **)dest, (void *)value, (void *)expected);
}

void *atomicCompareExchangePtr(void **dest, void *expected, void *value)
{
    return _InterlockedCompareExchangePointer(dest, value, expected);
}

#elif defined(__GNUC__) || defined(__clang__)

intptr_t atomicExchangeNInt(intptr_t *dest, intptr_t value)
{
    return __sync_lock_test_and_set(dest, value);
}

uintptr_t atomicExchangeNUInt(uintptr_t *dest, uintptr_t value)
{
    return __sync_lock_test_and_set(dest, value);
}

void *atomicExchangePtr(void **dest, void *value)
{
    return __sync_lock_test_and_set(dest, value);
}

intptr_t atomicCompareExchangeNInt(intptr_t *dest, intptr_t expected, intptr_t value)
{
    return __sync_val_compare_and_swap(dest, expected, value);
}

uintptr_t atomicCompareExchangeNUInt(uintptr_t *dest, uintptr_t expected, uintptr_t value)
{
    return __sync_val_compare_and_swap(dest, expected, value);
}

void *atomicCompareExchangePtr(void **dest, void *expected, void *value)
{
    return __sync_val_compare_and_swap(dest, expected, value);
}

#else
#error "Std timing atomics require MSVC, GCC, or Clang intrinsics."
#endif
