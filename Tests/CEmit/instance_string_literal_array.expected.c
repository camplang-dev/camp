// file: instance_string_literal_array.c
#include "instance_string_literal_array_private.h"
#include "instance_string_literal_array.h"


void Logger_log(Logger *this, const char* value, uintptr_t value_length)
{
}

int main(void)
{
	Logger* logger = 0;
	Logger_log(logger, "Hello, Console!", 15);
	return 0;
}

// file: instance_string_literal_array.h
#ifndef INSTANCE_STRING_LITERAL_ARRAY_H_
#define INSTANCE_STRING_LITERAL_ARRAY_H_

#include "instance_string_literal_array_private.h"

void Logger_log(Logger *this, const char* value, uintptr_t value_length);
int main(void);

#endif
// file: instance_string_literal_array_private.h
#ifndef INSTANCE_STRING_LITERAL_ARRAY_PRIVATE_H_
#define INSTANCE_STRING_LITERAL_ARRAY_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Logger Logger;

/* Newtypes. */

/* Enums. */

/* Layouts. */
struct Logger
{
	char _camp_empty;
};

/* Function declarations. */
void Logger_log(Logger *this, const char* value, uintptr_t value_length);
int main(void);

/* Object declarations. */


#endif
