// file: erased_result_abi.c
#include "erased_result_abi_private.h"

/* Private file declarations. */
void external(const void *value, void *__result, uintptr_t sizeof_T);

void getPoint(void *result)
{
	Point p = (Point){ 3, 4 };
	(*(Point *)result) = p;
}

bool find(int index, void *__result)
{
	if ((index == 0))
	{
		(*(__camp_erased_storage_117913a4a09d6895 *)__result).value = 7;
		(*(__camp_erased_storage_117913a4a09d6895 *)__result).specified = true;
		return true;
	}
	{
		(*(__camp_erased_storage_117913a4a09d6895 *)__result).value = 0;
		(*(__camp_erased_storage_117913a4a09d6895 *)__result).specified = 0;
		return false;
	}
}

void *getOpaquePoint(void *value)
{
	return value;
}

void identity(const void *value, void *__result, uintptr_t sizeof_T, const char *typenameof_T)
{
	{
		__builtin_memcpy(__result, value, sizeof_T);
		return;
	}
}

bool maybe(const void *value, void *__result, uintptr_t sizeof_T)
{
	{
		__builtin_memcpy(__result, value, sizeof_T);
		return true;
	}
}

void optional(const void *source, bool *source_specified, void *target, bool *target_specified)
{
	{
		(*(__camp_erased_storage_117913a4a09d6895 *)target).value = (*(const __camp_erased_storage_117913a4a09d6895 *)source).value;
		(*(__camp_erased_storage_117913a4a09d6895 *)target).specified = (*(const __camp_erased_storage_117913a4a09d6895 *)source).specified;
		(*target_specified) = (*source_specified);
	}
}

int externalCall(void)
{
	int _value0;
	external(&(int){3}, &_value0, sizeof(int));
	return _value0;
}

// file: erased_result_abi.h
#ifndef ERASED_RESULT_ABI_H_
#define ERASED_RESULT_ABI_H_

#include "erased_result_abi_private.h"

void getPoint(void *result);
bool find(int index, void *__result);
void *getOpaquePoint(void *value);
void identity(const void *value, void *__result, uintptr_t sizeof_T, const char *typenameof_T);
bool maybe(const void *value, void *__result, uintptr_t sizeof_T);
void optional(const void *source, bool *source_specified, void *target, bool *target_specified);
int externalCall(void);

#endif
// file: erased_result_abi_private.h
#ifndef ERASED_RESULT_ABI_PRIVATE_H_
#define ERASED_RESULT_ABI_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Point Point;

/* Enums. */

/* Newtypes. */

/* Layouts. */
struct Point
{
	int x;
	int y;
};

/* Erased storage layouts. */
typedef struct { int value; bool specified; } __camp_erased_storage_117913a4a09d6895;

/* Function declarations. */
void getPoint(void *result);
bool find(int index, void *__result);
void *getOpaquePoint(void *value);
void identity(const void *value, void *__result, uintptr_t sizeof_T, const char *typenameof_T);
bool maybe(const void *value, void *__result, uintptr_t sizeof_T);
void optional(const void *source, bool *source_specified, void *target, bool *target_specified);
int externalCall(void);

/* Object declarations. */


#endif
