// file: array_pointer_index.c
#include "array_pointer_index_private.h"

/* Private file declarations. */
static int read(int **source, uintptr_t *source_length, uintptr_t index);
static int readElements(int **source, uintptr_t *source_length, uintptr_t index);
static int readImplicitElements(int **source, uintptr_t *source_length, uintptr_t index);
static void write(int **source, uintptr_t *source_length, int value);
static int readConst(const int **source, uintptr_t *source_length);

static int read(int **source, uintptr_t *source_length, uintptr_t index)
{
	return (*source)[index];
}

static int readElements(int **source, uintptr_t *source_length, uintptr_t index)
{
	return (*source)[index];
}

static int readImplicitElements(int **source, uintptr_t *source_length, uintptr_t index)
{
	return (*source)[index];
}

static void write(int **source, uintptr_t *source_length, int value)
{
	(*source)[1] = value;
}

static int readConst(const int **source, uintptr_t *source_length)
{
	return (*source)[2];
}

// file: array_pointer_index_private.h
#ifndef ARRAY_POINTER_INDEX_PRIVATE_H_
#define ARRAY_POINTER_INDEX_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Enums. */

/* Newtypes. */

/* Layouts. */

/* Function declarations. */

/* Object declarations. */


#endif
