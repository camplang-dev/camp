// file: async_frame_lowering.c
#include "async_frame_lowering_private.h"

/* Private file declarations. */
typedef struct combine_asyncFrame combine_asyncFrame;
struct combine_asyncFrame
{
	int state;
	void (* complete)(void *arg0, int arg1);
	void *complete_context;
	int first;
	int second;
	int third;
	int await0_result;
	int await1_result;
};
static void combine_asyncResume(void *context);
static void combine_asyncComplete0(void *context, int result);
static void combine_asyncComplete1(void *context, int result);
void *malloc(uintptr_t size);
void free(void *ptr);
static void addOne(int value, void (* complete)(void *context, int result), void *complete_context);
static void combine(int first, void (* complete)(void *context, int result), void *complete_context);

static void combine_asyncComplete0(void *context, int result)
{
	combine_asyncFrame *frame = (combine_asyncFrame *)context;
	frame->await0_result = result;
	combine_asyncResume(frame);
}

static void combine_asyncComplete1(void *context, int result)
{
	combine_asyncFrame *frame = (combine_asyncFrame *)context;
	frame->await1_result = result;
	combine_asyncResume(frame);
}

static void combine_asyncResume(void *context)
{
	combine_asyncFrame *frame = (combine_asyncFrame *)context;
	switch (frame->state)
	{
		case 0: goto __async_state0;
		case 1: goto __async_state1;
		case 2: goto __async_state2;
	}
__async_state0: ;
	frame->state = 1;
	addOne(frame->first, combine_asyncComplete0, frame);
	return;
__async_state1: ;
	frame->second = frame->await0_result;
	frame->state = 2;
	addOne(frame->second, combine_asyncComplete1, frame);
	return;
__async_state2: ;
	frame->third = frame->await1_result;
	frame->complete(frame->complete_context, (frame->third + frame->first));
	free(frame);
	return;
}

static void addOne(int value, void (* complete)(void *context, int result), void *complete_context)
{
	complete(complete_context, (value + 1));
	return;
}

static void combine(int first, void (* complete)(void *context, int result), void *complete_context)
{
	combine_asyncFrame *frame = NULL;
	if (frame == NULL)
	{
		frame = (combine_asyncFrame *)malloc(sizeof(combine_asyncFrame));
	}
	*frame = (combine_asyncFrame){0};
	frame->complete = complete;
	frame->complete_context = complete_context;
	frame->first = first;
	combine_asyncResume(frame);
	return;
}

// file: async_frame_lowering_private.h
#ifndef ASYNC_FRAME_LOWERING_PRIVATE_H_
#define ASYNC_FRAME_LOWERING_PRIVATE_H_

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
