// file: static_string_literal_array.c
#include "static_string_literal_array_private.h"
#include "static_string_literal_array.h"


void Console_writeLine(const char* value, uintptr_t value_length)
{
}

int main(void)
{
	Console_writeLine("hello", 5);
	return 0;
}

// file: static_string_literal_array.h
#ifndef STATIC_STRING_LITERAL_ARRAY_H_
#define STATIC_STRING_LITERAL_ARRAY_H_

#include "static_string_literal_array_private.h"

void Console_writeLine(const char* value, uintptr_t value_length);
int main(void);

#endif
// file: static_string_literal_array_private.h
#ifndef STATIC_STRING_LITERAL_ARRAY_PRIVATE_H_
#define STATIC_STRING_LITERAL_ARRAY_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Console Console;

/* Newtypes. */

/* Enums. */

/* Layouts. */
struct Console
{
	char _camp_empty;
};

/* Function declarations. */
void Console_writeLine(const char* value, uintptr_t value_length);
int main(void);

/* Object declarations. */


#endif
