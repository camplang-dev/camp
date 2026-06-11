// file: primitive_flattened_symbols.c
#include "primitive_flattened_symbols_private.h"

/* Private file declarations. */
static bool String_tryParseInt(const char* this, int *value);
static uint32_t UInt_next(uint32_t this);
static uintptr_t NUInt_wideNext(uintptr_t this);
static void IntArray_touch(const int* this, uintptr_t this_length);
static bool CustomStringName(const char* this);

static bool String_tryParseInt(const char* this, int *value)
{
	(*value) = 0;
	return false;
}

static uint32_t UInt_next(uint32_t this)
{
	return (this + 1);
}

static uintptr_t NUInt_wideNext(uintptr_t this)
{
	return (this + 1);
}

static void IntArray_touch(const int* this, uintptr_t this_length)
{
}

static bool CustomStringName(const char* this)
{
	return true;
}

int main(void)
{
	int value;
	bool parsed = String_tryParseInt("42", &value);
	uint32_t one = UInt_next(0);
	uintptr_t two = NUInt_wideNext(((uintptr_t)(0)));
	int* values = (int []){1, 2, 3};
	uintptr_t values_length = 3;
	IntArray_touch(values, values_length);
	bool named = CustomStringName("x");
	if ((parsed || named))
	{
		return ((value + (int)(one)) + (int)(two));
	}
	return 0;
}

// file: primitive_flattened_symbols.h
#ifndef PRIMITIVE_FLATTENED_SYMBOLS_H_
#define PRIMITIVE_FLATTENED_SYMBOLS_H_

#include "primitive_flattened_symbols_private.h"

int main(void);

#endif
// file: primitive_flattened_symbols_private.h
#ifndef PRIMITIVE_FLATTENED_SYMBOLS_PRIVATE_H_
#define PRIMITIVE_FLATTENED_SYMBOLS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Newtypes. */

/* Enums. */

/* Layouts. */

/* Function declarations. */
int main(void);

/* Object declarations. */


#endif
