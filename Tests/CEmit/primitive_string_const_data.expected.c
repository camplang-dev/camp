// file: primitive_string_const_data.c
#include "primitive_string_const_data_private.h"


const char *echo(const char *value)
{
	return value;
}

void takesChars(const char *value)
{
}

void main(void)
{
	const char *value = "hello";
	takesChars(value);
}

// file: primitive_string_const_data.h
#ifndef PRIMITIVE_STRING_CONST_DATA_H_
#define PRIMITIVE_STRING_CONST_DATA_H_

#include "primitive_string_const_data_private.h"

const char *echo(const char *value);
void takesChars(const char *value);
void main(void);

#endif
// file: primitive_string_const_data_private.h
#ifndef PRIMITIVE_STRING_CONST_DATA_PRIVATE_H_
#define PRIMITIVE_STRING_CONST_DATA_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */

/* Newtypes. */

/* Enums. */

/* Layouts. */

/* Function declarations. */
const char *echo(const char *value);
void takesChars(const char *value);
void main(void);

/* Object declarations. */


#endif
