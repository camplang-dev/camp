// file: lifecycle_delete.c
#include "lifecycle_delete_private.h"
#include "lifecycle_delete.h"

/* Private file declarations. */
void* malloc(uintptr_t size);
void free(void* ptr);
static void Logger_op_initnew(Logger *this);
static Logger* Logger_create(void);
static void Logger_op_delete(Logger *this);
static void Logger_destroy(Logger *this);

static void Logger_op_initnew(Logger *this)
{
}

static Logger* Logger_create(void)
{
	Logger* _created0 = (Logger *)(malloc(sizeof(Logger)));
	if ((_created0 != NULL))
	{
		Logger_op_initnew(_created0);
	}
	return _created0;
}

static void Logger_op_delete(Logger *this)
{
}

static void Logger_destroy(Logger *this)
{
	Logger_op_delete(this);
	free((void *)(this));
}

void run(void)
{
	Logger* logger = (Logger *)(malloc(sizeof(Logger)));
	if ((logger != NULL))
	{
		Logger_op_initnew(logger);
	}
	(Logger_op_delete(logger), free((void *)(logger)));
}

// file: lifecycle_delete.h
#ifndef LIFECYCLE_DELETE_H_
#define LIFECYCLE_DELETE_H_

#include "lifecycle_delete_private.h"

void run(void);

#endif
// file: lifecycle_delete_private.h
#ifndef LIFECYCLE_DELETE_PRIVATE_H_
#define LIFECYCLE_DELETE_PRIVATE_H_

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
void run(void);

/* Object declarations. */


#endif
