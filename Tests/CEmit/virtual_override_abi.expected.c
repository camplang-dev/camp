// file: virtual_override_abi.c
#include "virtual_override_abi_private.h"

/* Private file declarations. */
static void *Allocator_alloc(Allocator *this, uintptr_t size);
static void Allocator_free(Allocator *this, void *ptr);
static void *HeapAllocator_alloc(HeapAllocator *this, uintptr_t size);
static void HeapAllocator_free(HeapAllocator *this, void *ptr);
static void *HeapAllocator__alloc(Allocator *ctx, uintptr_t size);
static void HeapAllocator__free(Allocator *ctx, void *ptr);
static int BaseCounter_value(BaseCounter *this);
static int BaseCounter__value(BaseCounter *this);
static int Counter_value(Counter *this);
static int Counter__value(BaseCounter *ctx);
static _Allocator _Allocator__vt;
static _HeapAllocator _HeapAllocator__vt;
static _BaseCounter _BaseCounter__vt;
static _Counter _Counter__vt;

static _Allocator _Allocator__vt = { .alloc = NULL, .free = NULL };
static _HeapAllocator _HeapAllocator__vt = { .Allocator = { .alloc = HeapAllocator__alloc, .free = HeapAllocator__free } };
static _BaseCounter _BaseCounter__vt = { .value = BaseCounter__value };
static _Counter _Counter__vt = { .BaseCounter = { .value = Counter__value } };
static void *Allocator_alloc(Allocator *this, uintptr_t size)
{
	return this->_vt->alloc(this, size);
}

static void Allocator_free(Allocator *this, void *ptr)
{
	this->_vt->free(this, ptr);
}

static void *HeapAllocator__alloc(Allocator *ctx, uintptr_t size)
{
	HeapAllocator *this = (HeapAllocator *)(ctx);
	(void)this;
	return NULL;
}

static void HeapAllocator__free(Allocator *ctx, void *ptr)
{
	HeapAllocator *this = (HeapAllocator *)(ctx);
	(void)this;
}

static int BaseCounter_value(BaseCounter *this)
{
	return this->_vt->value(this);
}

static int BaseCounter__value(BaseCounter *this)
{
	return 1;
}

static int Counter__value(BaseCounter *ctx)
{
	Counter *this = (Counter *)(ctx);
	(void)this;
	return (BaseCounter__value((BaseCounter *)(this)) + 1);
}

// file: virtual_override_abi_private.h
#ifndef VIRTUAL_OVERRIDE_ABI_PRIVATE_H_
#define VIRTUAL_OVERRIDE_ABI_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Allocator Allocator;
typedef struct HeapAllocator HeapAllocator;
typedef struct BaseCounter BaseCounter;
typedef struct Counter Counter;
typedef struct _Allocator _Allocator;
typedef struct _HeapAllocator _HeapAllocator;
typedef struct _BaseCounter _BaseCounter;
typedef struct _Counter _Counter;

/* Enums. */

/* Newtypes. */

/* Callable typedefs. */
typedef int (* fn_int_BaseCounterPtr_)(BaseCounter *arg0);
typedef void (* fn_void_AllocatorPtr__voidPtr_)(Allocator *arg0, void *arg1);
typedef void *(* fn_voidPtr_AllocatorPtr__nuint_)(Allocator *arg0, uintptr_t arg1);

/* Layouts. */
struct Allocator
{
	_Allocator *_vt;
};
struct HeapAllocator
{
	_Allocator *_vt;
};
struct BaseCounter
{
	_BaseCounter *_vt;
};
struct Counter
{
	_BaseCounter *_vt;
};
struct _Allocator
{
	void *(* alloc)(Allocator *ctx, uintptr_t size);
	void (* free)(Allocator *ctx, void *ptr);
};
struct _HeapAllocator
{
	_Allocator Allocator;
};
struct _BaseCounter
{
	int (* value)(BaseCounter *ctx);
};
struct _Counter
{
	_BaseCounter BaseCounter;
};

/* Function declarations. */

/* Object declarations. */


#endif
