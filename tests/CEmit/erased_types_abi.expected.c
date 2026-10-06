// file: erased_types_abi.c
#include "erased_types_abi_private.h"

/* Private file declarations. */
static void main(void);

void *identity(void *p)
{
	return p;
}

const void *readonlyIdentity(const void *p)
{
	return p;
}

void **deeper(void **p)
{
	return p;
}

void array(void *values, uintptr_t values_length)
{
}

int input(const void *value)
{
	return (*(const int *)value);
}

void output(void *value)
{
	(*(int *)value) = 2;
}

int receiver(void *this)
{
	return (*(int *)(this));
}

void callback(void (* action)(const void *input, void *output))
{
}

static void main(void)
{
}

// file: erased_types_abi.h
#ifndef ERASED_TYPES_ABI_H_
#define ERASED_TYPES_ABI_H_

#include "erased_types_abi_private.h"

void *identity(void *p);
const void *readonlyIdentity(const void *p);
void **deeper(void **p);
void array(void *values, uintptr_t values_length);
int input(const void *value);
void output(void *value);
int receiver(void *this);
void callback(void (* action)(const void *input, void *output));

#endif
// file: erased_types_abi_private.h
#ifndef ERASED_TYPES_ABI_PRIVATE_H_
#define ERASED_TYPES_ABI_PRIVATE_H_

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
};

/* Erased storage layouts. */
typedef struct { int value; bool specified; } __camp_erased_storage_117913a4a09d6895;

/* Function declarations. */
void *identity(void *p);
const void *readonlyIdentity(const void *p);
void **deeper(void **p);
void array(void *values, uintptr_t values_length);
int input(const void *value);
void output(void *value);
int receiver(void *this);
void callback(void (* action)(const void *input, void *output));

/* Object declarations. */


#endif
