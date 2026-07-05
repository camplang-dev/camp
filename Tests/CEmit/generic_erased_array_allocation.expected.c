// file: generic_erased_array_allocation.c
#include "generic_erased_array_allocation_private.h"

/* Private file declarations. */
void *malloc(uintptr_t size);
void free(void *ptr);
static void Buffer_op_initnew(Buffer *this, uintptr_t sizeof_T);
static Buffer *Buffer_create(uintptr_t sizeof_T);
static void Buffer_op_delete(Buffer *this);
static void Buffer_destroy(Buffer *this);

static void Buffer_op_initnew(Buffer *this, uintptr_t sizeof_T)
{
	this->_sizeof_T = sizeof_T;
	{
		this->items = (void*)(malloc((sizeof_T * 4)));
		this->items_length = 4;
	}
}

static Buffer *Buffer_create(uintptr_t sizeof_T)
{
	Buffer *_created0 = (Buffer *)(malloc(sizeof(Buffer)));
	if ((_created0 != NULL))
	{
		*_created0 = (Buffer){0};
		Buffer_op_initnew(_created0, sizeof_T);
	}
	return _created0;
}

static void Buffer_op_delete(Buffer *this)
{
	free((void *)(this->items));
}

static void Buffer_destroy(Buffer *this)
{
	Buffer_op_delete(this);
	free((void *)(this));
}

void make(void)
{
	Buffer *buffer = (Buffer *)(malloc(sizeof(Buffer)));
	if ((buffer != NULL))
	{
		*buffer = (Buffer){0};
		Buffer_op_initnew(buffer, sizeof(int));
	}
	(Buffer_op_delete(buffer), free((void *)(buffer)));
}

// file: generic_erased_array_allocation.h
#ifndef GENERIC_ERASED_ARRAY_ALLOCATION_H_
#define GENERIC_ERASED_ARRAY_ALLOCATION_H_

#include "generic_erased_array_allocation_private.h"

void make(void);

#endif
// file: generic_erased_array_allocation_private.h
#ifndef GENERIC_ERASED_ARRAY_ALLOCATION_PRIVATE_H_
#define GENERIC_ERASED_ARRAY_ALLOCATION_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Buffer Buffer;

/* Enums. */

/* Newtypes. */

/* Layouts. */
struct Buffer
{
	void *items;
	uintptr_t items_length;
	uintptr_t _sizeof_T;
};

/* Function declarations. */
void make(void);

/* Object declarations. */


#endif
