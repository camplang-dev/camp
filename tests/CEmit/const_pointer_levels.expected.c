// file: const_pointer_levels.c
#include "const_pointer_levels_private.h"

/* Private file declarations. */
static int Item_getValue(const Item *this);

int readConstPointer(int * const *value)
{
	return **value;
}

int readConstToConst(const int * const *value)
{
	return **value;
}

int readVolatilePointer(int * volatile *value)
{
	return **value;
}

int readDeepPointer(int * const * const *value)
{
	return ***value;
}

static int Item_getValue(const Item *this)
{
	return this->value;
}

int readConstItem(Item * const value)
{
	return value->value;
}

int readVolatileItem(Item * volatile value)
{
	return value->value;
}

int readIndirectItem(Item * const *value)
{
	return (*value)->value;
}

int callConstItem(Item * const value)
{
	return Item_getValue(value);
}

int callQualifiedItem(const Item * const value)
{
	return Item_getValue((const Item *)(value));
}

int callIndirectItem(Item * const *value)
{
	return Item_getValue((*value));
}

int callValueItem(Item value)
{
	return Item_getValue(&value);
}

// file: const_pointer_levels.h
#ifndef CONST_POINTER_LEVELS_H_
#define CONST_POINTER_LEVELS_H_

#include "const_pointer_levels_private.h"

int readConstPointer(int * const *value);
int readConstToConst(const int * const *value);
int readVolatilePointer(int * volatile *value);
int readDeepPointer(int * const * const *value);
int readConstItem(Item * const value);
int readVolatileItem(Item * volatile value);
int readIndirectItem(Item * const *value);
int callConstItem(Item * const value);
int callQualifiedItem(const Item * const value);
int callIndirectItem(Item * const *value);
int callValueItem(Item value);

#endif
// file: const_pointer_levels_private.h
#ifndef CONST_POINTER_LEVELS_PRIVATE_H_
#define CONST_POINTER_LEVELS_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Item Item;

/* Enums. */

/* Newtypes. */

/* Layouts. */
struct Item
{
	int value;
};

/* Function declarations. */
int readConstPointer(int * const *value);
int readConstToConst(const int * const *value);
int readVolatilePointer(int * volatile *value);
int readDeepPointer(int * const * const *value);
int readConstItem(Item * const value);
int readVolatileItem(Item * volatile value);
int readIndirectItem(Item * const *value);
int callConstItem(Item * const value);
int callQualifiedItem(const Item * const value);
int callIndirectItem(Item * const *value);
int callValueItem(Item value);

/* Object declarations. */


#endif
