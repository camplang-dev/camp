// file: async_tail_await_forwarding.c
#include "async_tail_await_forwarding_private.h"

/* Private file declarations. */
static void Resumer_resumeAsync(Resumer *this, void (* continuation)(void *arg0), void *continuation_context);
static void inner(Resumer *resumer, void (* complete)(void *context, int result), void *complete_context);
static void outer(Resumer *resumer, void (* complete)(void *context, int result), void *complete_context);

static void Resumer_resumeAsync(Resumer *this, void (* continuation)(void *arg0), void *continuation_context)
{
	continuation(continuation_context);
}

static void inner(Resumer *resumer, void (* complete)(void *context, int result), void *complete_context)
{
	complete(complete_context, 11);
	return;
}

static void outer(Resumer *resumer, void (* complete)(void *context, int result), void *complete_context)
{
	inner(resumer, complete, complete_context);
	return;
}

// file: async_tail_await_forwarding_private.h
#ifndef ASYNC_TAIL_AWAIT_FORWARDING_PRIVATE_H_
#define ASYNC_TAIL_AWAIT_FORWARDING_PRIVATE_H_

#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

/* Forward declarations. */
typedef struct Resumer Resumer;

/* Enums. */

/* Newtypes. */

/* Layouts. */
struct Resumer
{
	char _camp_empty;
};

/* Function declarations. */

/* Object declarations. */


#endif
