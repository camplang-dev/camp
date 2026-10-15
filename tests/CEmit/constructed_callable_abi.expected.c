// file: constructed_callable_abi.c
#include "constructed_callable_abi_private.h"


int narrow(int value)
{
	return value;
}

int64_t wide(int64_t value)
{
	return value;
}

intptr_t native(intptr_t value)
{
	return value;
}

int named(void *item, const char *typenameof_V, uintptr_t sizeof_V)
{
	return 1;
}

int witnessed(void *item, const Reader *vtableof_V_Reader)
{
	return vtableof_V_Reader->read(item);
}

bool copying(const void *value, void *slot, uintptr_t sizeof_V)
{
	__builtin_memcpy(slot, value, sizeof_V);
	return true;
}

int nested(Narrow inner, int value)
{
	return inner(value);
}

int Holder_bound(Holder *this, int value)
{
	return value;
}

Narrow pick(void)
{
	return (Narrow)narrow;
}

Bound referenceBound(Holder *holder, void **result_context)
{
	{
		(*result_context) = (void *)(holder);
		return (int (*)(void *arg0, int arg1))(Holder_bound);
	}
}

// file: constructed_callable_abi.h
#ifndef CONSTRUCTED_CALLABLE_ABI_H_
#define CONSTRUCTED_CALLABLE_ABI_H_

#include "constructed_callable_abi_private.h"

int narrow(int value);
int64_t wide(int64_t value);
intptr_t native(intptr_t value);
int named(void *item, const char *typenameof_V, uintptr_t sizeof_V);
int witnessed(void *item, const Reader *vtableof_V_Reader);
bool copying(const void *value, void *slot, uintptr_t sizeof_V);
int nested(Narrow inner, int value);
int Holder_bound(Holder *this, int value);
Narrow pick(void);
Bound referenceBound(Holder *holder, void **result_context);

#endif
// file: constructed_callable_abi_private.h
#ifndef CONSTRUCTED_CALLABLE_ABI_PRIVATE_H_
#define CONSTRUCTED_CALLABLE_ABI_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Reader Reader;
typedef struct Holder Holder;

/* Enums. */

/* Newtypes. */
typedef int (* Narrow)(int value);
typedef int64_t (* Wide)(int64_t value);
typedef intptr_t (* Native)(intptr_t value);
typedef int (* Bound)(void *context, int value);
typedef int (* Named)(void *item, const char *typenameof_U, uintptr_t sizeof_U);
typedef int (* Witnessed)(void *item, const Reader *vtableof_U_Reader);
typedef bool (* Copying)(const void *value, void *slot, uintptr_t sizeof_U);
typedef int (* Nested)(Narrow inner, int value);

/* Layouts. */
struct Reader
{
	int (* read)(void* ctx);
};
struct Holder
{
	char _camp_empty;
};

/* Function declarations. */
int narrow(int value);
int64_t wide(int64_t value);
intptr_t native(intptr_t value);
int named(void *item, const char *typenameof_V, uintptr_t sizeof_V);
int witnessed(void *item, const Reader *vtableof_V_Reader);
bool copying(const void *value, void *slot, uintptr_t sizeof_V);
int nested(Narrow inner, int value);
int Holder_bound(Holder *this, int value);
Narrow pick(void);
Bound referenceBound(Holder *holder, void **result_context);

/* Object declarations. */


#endif
